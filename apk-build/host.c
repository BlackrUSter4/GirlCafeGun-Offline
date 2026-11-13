// libsssjhost.so — standalone host executable (lives in nativeLibDir, exec-allowed).
// The shim fork+execs this as a SEPARATE process so CoreCLR/Mono runs isolated from il2cpp.
// It extracts the DLLs+setting, symlinks the runtime .so, and hosts SssjServer.App.dll via hostfxr.
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/stat.h>
#include <malloc.h>
#include <android/log.h>

#define TAG "sssjhost"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

#define PKG "com.biligameglobal.girlcafegungp"
#define SRVDIR "/data/data/" PKG "/files/sssjsrv"

int main(int argc, char** argv) {
    (void)argc; (void)argv;
    LOGI("host start (server dir %s)", SRVDIR);
    chdir(SRVDIR);
    freopen(SRVDIR "/srv.log", "w", stdout);
    freopen(SRVDIR "/srv.log", "a", stderr);
    // Real arm64 phones (Android 11+) tag heap pointers (TBI). The Mono runtime shifts pointers and
    // crashes in monovm_initialize with a tagged one (seen on a Galaxy S24+, Android 17). Turn tagging
    // off before the runtime allocates anything. Emulators have no TBI, so they never hit this.
    LOGI("heap tagging off rc=%d", mallopt(M_BIONIC_SET_HEAP_TAGGING_LEVEL, M_HEAP_TAGGING_LEVEL_NONE));
    setenv("DOTNET_EnableWriteXorExecute", "0", 1);
    setenv("HOME", SRVDIR, 1);
    setenv("DOTNET_gcServer", "0", 1);
    setenv("DOTNET_ROOT", SRVDIR, 1);
    setenv("COREHOST_TRACE", "1", 1);
    setenv("COREHOST_TRACEFILE", SRVDIR "/trace.log", 1);

    char fxrp[1200]; snprintf(fxrp, sizeof(fxrp), "%s/libhostfxr.so", SRVDIR);
    void* fxr = dlopen(fxrp, RTLD_NOW | RTLD_GLOBAL);
    if (!fxr) { LOGE("dlopen hostfxr failed: %s", dlerror()); return 2; }
    typedef struct { size_t size; const char* host_path; const char* dotnet_root; } init_params;
    typedef int (*init_fn)(int, const char**, const init_params*, void**);
    typedef int (*run_fn)(void*);
    init_fn init = (init_fn)dlsym(fxr, "hostfxr_initialize_for_dotnet_command_line");
    run_fn run = (run_fn)dlsym(fxr, "hostfxr_run_app");
    if (!init || !run) { LOGE("hostfxr symbols missing"); return 3; }
    const char* a[] = { SRVDIR "/SssjServer.App.dll", "--setting-dir", SRVDIR "/setting",
        // Saves live OUTSIDE SRVDIR: the shim runs `rm -rf SRVDIR` and re-extracts on every launch.
        "--data-dir", "/data/data/" PKG "/files/sssjsave", "--gate-host", "127.0.0.1", "--http-port", "9080",
        "--gate-port", "9600", "--gm-accounts", "none", "--skip-tutorial", "false",
        "--grant-starter", "true", "--all-events-open", "true" };
    // host_path/dotnet_root must point at the server dir (filesDir) where the DLLs live — otherwise
    // the runtime resolves relative to /proc/self/exe (nativeLibDir, no DLLs) and corlib is not found.
    init_params ip = { sizeof(init_params), SRVDIR "/SssjServer.App.dll", SRVDIR };
    void* ctx = 0;
    int rc = init((int)(sizeof(a) / sizeof(a[0])), a, &ip, &ctx);
    LOGI("hostfxr init rc=0x%x ctx=%p", rc, ctx);
    if ((rc == 0 || rc == 1) && ctx) { LOGI("running server"); int r = run(ctx); LOGE("server exited r=%d", r); return r; }
    LOGE("hostfxr init failed rc=0x%x", rc);
    return 4;
}
