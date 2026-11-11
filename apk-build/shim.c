// libmain.so shim for GCG offline embedding.
// On load: (1) start the sssj-new server as a child process, (2) patch il2cpp (via /proc/self/mem,
// so reads can't SIGSEGV and writes bypass page protection) so the client talks http://127.0.0.1:9080
// offline + takes the offline account path, (3) chain the real libmain (renamed libmain_orig.so).
#include <jni.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <stdint.h>
#include <fcntl.h>
#include <dirent.h>
#include <sys/stat.h>
#include <sys/mman.h>
#include <sys/uio.h>
#include <sys/syscall.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <android/log.h>

#define TAG "sssjshim"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

#define PKG "com.biligameglobal.girlcafegungp"
#define FILESDIR "/data/data/" PKG "/files"
#define SRVDIR FILESDIR "/sssjsrv"

// il2cpp arm64 offsets (from client/launch_gcg.ps1, verified 2026-11-07)
#define PKG_TI   0x505EDB0
#define SRV_TI   0x5062860
#define SF_OFF   0xB8
#define INST_OFF 0x10
#define DISGM_OFF 0x42
#define PLAT_OFF 0x30

static char g_libdir[512];

static uintptr_t find_il2cpp_base(void) {
    // /proc/self/maps can list libil2cpp.so several times under houdini; the real load base is the
    // mapping whose file offset (3rd column) is 0. Match that, not just the first occurrence.
    FILE* f = fopen("/proc/self/maps", "r");
    if (!f) return 0;
    char line[1024]; uintptr_t base = 0;
    while (fgets(line, sizeof(line), f)) {
        if (strstr(line, "lib/arm64/libil2cpp.so") || strstr(line, "libil2cpp.so")) {
            unsigned long lo = 0, off = 1;
            if (sscanf(line, "%lx-%*lx %*s %lx", &lo, &off) == 2 && off == 0) { base = (uintptr_t)lo; break; }
        }
    }
    fclose(f);
    return base;
}
// Read via /proc/self/mem (O_RDONLY) — the kernel's own view, same as the external launcher's
// /proc/pid/mem read, and reliable where process_vm_readv is flaky under houdini. Fall back to
// process_vm_readv. Both are SIGSEGV-safe (they return an error on unmapped pages). Writes go to the
// il2cpp managed heap (writable), so a guarded direct write is fine.
static int g_memfd = -2;
static int memrd(uintptr_t a, void* b, size_t n) {
    if (g_memfd == -2) g_memfd = open("/proc/self/mem", O_RDONLY);
    if (g_memfd >= 0 && pread(g_memfd, b, n, (off_t)a) == (ssize_t)n) return 0;
    struct iovec lo = { b, n }, re = { (void*)a, n };
    return (ssize_t)syscall(SYS_process_vm_readv, getpid(), &lo, 1UL, &re, 1UL, 0UL) == (ssize_t)n ? 0 : -1;
}
static int memwr(uintptr_t a, const void* b, size_t n) {
    uintptr_t pg = a & ~(uintptr_t)0xFFF; mprotect((void*)pg, 0x2000, PROT_READ | PROT_WRITE);
    memcpy((void*)a, b, n); return 0;
}
static uintptr_t rdptr(uintptr_t a) { uintptr_t v = 0; return memrd(a, &v, 8) == 0 ? v : 0; }

// Close the client's hung socket(s) to 127.0.0.1:9080 so it retries (over http once disablegm=0).
static int kill_9080(void) {
    DIR* d = opendir("/proc/self/fd"); if (!d) return 0;
    struct dirent* e; int n = 0;
    while ((e = readdir(d))) {
        int fd = atoi(e->d_name); if (fd <= 2) continue;
        struct sockaddr_storage ss; socklen_t sl = sizeof(ss);
        if (getpeername(fd, (struct sockaddr*)&ss, &sl) != 0) continue;
        int port = -1;
        if (ss.ss_family == AF_INET) port = ntohs(((struct sockaddr_in*)&ss)->sin_port);
        else if (ss.ss_family == AF_INET6) port = ntohs(((struct sockaddr_in6*)&ss)->sin6_port);
        if (port == 9080) { close(fd); n++; }
    }
    closedir(d);
    return n;
}

static void* patch_thread(void* arg) {
    (void)arg;
    uintptr_t base = 0;
    for (int i = 0; i < 400 && !base; i++) { base = find_il2cpp_base(); if (!base) usleep(250000); }
    if (!base) { LOGE("libil2cpp not mapped"); return 0; }
    LOGI("il2cpp base=0x%lx", (unsigned long)base);

    // Find PackageInfo.Instance AND keep re-applying the patch in one long loop (~90s). PackageInfo
    // initializes late on a slow cold boot (seconds to tens of seconds after il2cpp maps), and after it
    // does, PackageInfo.Load can overwrite platform back — so a one-shot find+patch loses the race on a
    // cold device. The loop re-resolves Instance every 30ms, patches whenever it's present, and keeps
    // forcing disablegm=0 + platform=selftest until the client has built the serverlist URL as
    // http://127.0.0.1. Winning this race is what makes the client talk to the embedded server.
    // Up to ~5 min: PackageInfo can initialize very late on a cold/slow device, and the config patch
    // (disablegm=0 → local http) must land before the client's first reachability-gated sync, or that
    // sync hits the online URL, fails offline, and retries forever. A generous window covers slow boots.
    int urlpatched = 0, instlogged = 0;
    for (int iter = 0; iter < 10000; iter++) {
        uintptr_t cls = rdptr(base + PKG_TI);
        uintptr_t sf = cls ? rdptr(cls + SF_OFF) : 0;
        uintptr_t in = sf ? rdptr(sf + INST_OFF) : 0;
        if (in) {
            if (!instlogged) { LOGI("PackageInfo.Instance=0x%lx (iter %d)", (unsigned long)in, iter); instlogged = 1; }
            uint8_t zero = 0; memwr(in + DISGM_OFF, &zero, 1);                 // disablegm = false
            uintptr_t plat = rdptr(in + PLAT_OFF);                            // platform -> "selftest"
            if (plat) {
                int plen = 0; memrd(plat + 0x10, &plen, 4);
                if (plen >= 8) {
                    int nlen = 8; memwr(plat + 0x10, &nlen, 4);
                    uint16_t pch[9]; const char* s = "selftest";
                    for (int i = 0; i < 8; i++) pch[i] = (uint16_t)s[i]; pch[8] = 0;
                    memwr(plat + 0x14, pch, 18);
                }
            }
            uintptr_t slcls = rdptr(base + SRV_TI);                           // ServerList.sUrl scheme + sPlatform
            if (slcls) {
                uintptr_t slsf = rdptr(slcls + SF_OFF);
                if (slsf) {
                    uintptr_t surl = rdptr(slsf + 0x0);
                    if (surl) {
                        uint16_t uch[22]; memrd(surl + 0x14, uch, 44);
                        const char* h = "https://127.0.0.1:9080"; int m = 1;
                        for (int i = 0; h[i]; i++) if (uch[i] != (uint16_t)h[i]) { m = 0; break; }
                        if (m) { const char* nu = "http://127.0.0.1:09080"; uint16_t w[22]; for (int i = 0; i < 22; i++) w[i] = (uint16_t)nu[i]; memwr(surl + 0x14, w, 44);
                            if (!urlpatched) { LOGI("patched: disablegm=0 platform=selftest url=http (iter %d)", iter); urlpatched = 1; } }
                    }
                    if (plat) memwr(slsf + 0x8, &plat, 8);
                }
            }
        }
        usleep(30000);
    }
    LOGI("patch loop done (urlpatched=%d)", urlpatched);
    (void)kill_9080;
    return 0;
}

static void* server_thread(void* arg) {
    (void)arg;
    char cmd[4096];
    mkdir(FILESDIR, 0771);
    snprintf(cmd, sizeof(cmd), "rm -rf '%s'; mkdir -p '%s'; cd '%s' && /system/bin/toybox tar xzf '%s/libsssjdata.so'", SRVDIR, SRVDIR, SRVDIR, g_libdir);
    LOGI("extract rc=%d", system(cmd));
    // The runtime .so ship in the native-library dir (exec-allowed under enforcing SELinux); symlink them
    // next to the app DLLs so the runtime resolves everything from one place. dlopen follows the symlink to
    // the real file in the lib dir, which SELinux permits an app to execute.
    const char* sos[] = { "libhostfxr.so","libhostpolicy.so","libcoreclr.so","libSystem.Native.so",
        "libSystem.Globalization.Native.so","libSystem.IO.Compression.Native.so",
        "libSystem.Security.Cryptography.Native.OpenSsl.so", 0 };
    for (int i = 0; sos[i]; i++) {
        char lt[1024], ln[1024];
        snprintf(lt, sizeof(lt), "%s/%s", g_libdir, sos[i]);
        snprintf(ln, sizeof(ln), "%s/%s", SRVDIR, sos[i]);
        unlink(ln); symlink(lt, ln);
    }
    char host[1024]; snprintf(host, sizeof(host), "%s/libsssjhost.so", g_libdir);
    pid_t pid = fork();
    if (pid == 0) { char* a[] = { host, 0 }; execv(host, a); _exit(127); }
    LOGI("forked host pid=%d", pid);
    return 0;
}

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) {
    Dl_info di;
    if (dladdr((void*)JNI_OnLoad, &di) && di.dli_fname) {
        strncpy(g_libdir, di.dli_fname, sizeof(g_libdir) - 1);
        char* sl = strrchr(g_libdir, '/'); if (sl) *sl = 0;
    }
    LOGI("JNI_OnLoad libdir=%s", g_libdir);
    pthread_t ps, pp;
    pthread_create(&ps, 0, server_thread, 0);
    pthread_create(&pp, 0, patch_thread, 0);
    char op[1024]; snprintf(op, sizeof(op), "%s/libmain_orig.so", g_libdir);
    void* orig = dlopen(op, RTLD_NOW | RTLD_GLOBAL);
    if (orig) {
        typedef jint (*jol)(JavaVM*, void*);
        jol f = (jol)dlsym(orig, "JNI_OnLoad");
        if (f) { jint v = f(vm, reserved); LOGI("chained libmain_orig JNI_OnLoad=%d", v); return v; }
        LOGE("libmain_orig has no JNI_OnLoad");
    } else LOGE("dlopen libmain_orig failed: %s", dlerror());
    return JNI_VERSION_1_6;
}
