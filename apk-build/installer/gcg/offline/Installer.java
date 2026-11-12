package gcg.offline;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.Fragment;
import android.app.FragmentManager;
import android.content.ContentResolver;
import android.content.Intent;
import android.database.Cursor;
import android.graphics.Color;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.os.StatFs;
import android.provider.DocumentsContract;
import android.provider.OpenableColumns;
import android.util.Log;
import android.view.Gravity;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;

/**
 * First-run installer for the offline build. libmain.so calls {@link #boot()} once from JNI_OnLoad.
 * It waits for the game's activity, and if the game data is missing it asks the player to pick the
 * data file (GirlCafeGun-data.gcg, an uncompressed tar) with the system file picker, then unpacks it:
 *   obb/...   -> getObbDir()                 (Android/obb/<pkg>/)
 *   files/... -> getExternalFilesDir(null)   (Android/data/<pkg>/files/)
 * No permission is needed: the picker grants access to the chosen file, and both targets are the app's own folders.
 */
public final class Installer {
    static final String TAG = "gcginstall";
    /** First tar entry of a valid data file: "GCGDATA" header file holding this version line. */
    static final String MAGIC = "GCGDATA";
    static final String MARKER = ".gcg_data_installed";
    static final int REQ = 0x4711;

    private Installer() {}

    /** Called from native code (any thread). */
    public static void boot() {
        final Handler h = new Handler(Looper.getMainLooper());
        h.post(new Runnable() {
            int tries = 0;
            public void run() {
                Activity a = currentActivity();
                if (a == null) {
                    if (++tries < 600) h.postDelayed(this, 100);
                    return;
                }
                try {
                    if (!isInstalled(a)) show(a);
                } catch (Throwable t) {
                    Log.e(TAG, "installer failed", t);
                }
            }
        });
    }

    static Activity currentActivity() {
        try {
            return (Activity) Class.forName("com.unity3d.player.UnityPlayer").getField("currentActivity").get(null);
        } catch (Throwable t) {
            return null;
        }
    }

    static File filesDir(Activity a) { return a.getExternalFilesDir(null); }

    static boolean isInstalled(Activity a) {
        File m = new File(filesDir(a), MARKER);
        File[] obbs = a.getObbDir() == null ? null : a.getObbDir().listFiles();
        boolean hasObb = false;
        if (obbs != null) for (File f : obbs) if (f.getName().endsWith(".obb") && f.length() > 100L * 1024 * 1024) hasObb = true;
        boolean ok = m.exists() && hasObb;
        Log.i(TAG, "installed=" + ok + " marker=" + m.exists() + " obb=" + hasObb);
        return ok;
    }

    static void show(Activity a) {
        FragmentManager fm = a.getFragmentManager();
        if (fm.findFragmentByTag(TAG) != null) return;
        fm.beginTransaction().add(new Host(), TAG).commitAllowingStateLoss();
    }

    /** Headless fragment: owns the dialogs and receives the file picker result without a manifest entry. */
    public static final class Host extends Fragment {
        AlertDialog dlg;
        ProgressBar bar;
        TextView text;

        @Override public void onCreate(android.os.Bundle b) {
            super.onCreate(b);
            setRetainInstance(true);
            ask(null);
        }

        void ask(String error) {
            final Activity a = getActivity();
            String msg = "This game needs its data file before the first start.\n\n"
                    + "1. Download GirlCafeGun-data.gcg to this phone.\n"
                    + "2. Tap \"Choose file\" and select it.\n\n"
                    + "Installing takes 5 to 15 minutes and needs about 5 GB of free space.";
            if (error != null) msg = error + "\n\n" + msg;
            dlg = new AlertDialog.Builder(a)
                    .setTitle("Install game data")
                    .setMessage(msg)
                    .setCancelable(false)
                    .setPositiveButton("Choose file", (d, w) -> pick())
                    .setNegativeButton("Quit", (d, w) -> quit())
                    .show();
        }

        void pick() {
            Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT);
            i.addCategory(Intent.CATEGORY_OPENABLE);
            i.setType("*/*");
            try {
                startActivityForResult(i, REQ);
            } catch (Throwable t) {
                ask("No file picker is available on this phone.");
            }
        }

        @Override public void onActivityResult(int req, int res, Intent data) {
            if (req != REQ) return;
            if (res != Activity.RESULT_OK || data == null || data.getData() == null) { ask(null); return; }
            install(data.getData());
        }

        void install(final Uri uri) {
            final Activity a = getActivity();
            LinearLayout box = new LinearLayout(a);
            box.setOrientation(LinearLayout.VERTICAL);
            int pad = (int) (20 * a.getResources().getDisplayMetrics().density);
            box.setPadding(pad, pad, pad, pad);
            text = new TextView(a);
            text.setText("Preparing...");
            text.setGravity(Gravity.START);
            bar = new ProgressBar(a, null, android.R.attr.progressBarStyleHorizontal);
            bar.setMax(1000);
            box.addView(text);
            box.addView(bar);
            dlg = new AlertDialog.Builder(a).setTitle("Installing game data").setView(box).setCancelable(false).show();
            new Thread(() -> {
                String err = null;
                long total = size(a.getContentResolver(), uri);
                try {
                    unpack(a, uri, total);
                } catch (Throwable t) {
                    Log.e(TAG, "install failed", t);
                    err = t.getMessage() == null ? t.toString() : t.getMessage();
                }
                final String e = err;
                a.runOnUiThread(() -> {
                    if (dlg != null) dlg.dismiss();
                    if (e != null) ask("Installation failed: " + e);
                    else done(uri, total);
                });
            }, "gcg-install").start();
        }

        void done(final Uri uri, long total) {
            final Activity a = getActivity();
            String gb = String.format(java.util.Locale.US, "%.1f GB", total / 1073741824.0);
            dlg = new AlertDialog.Builder(a)
                    .setTitle("Game data installed")
                    .setMessage("The game data file is no longer needed. Delete it to free " + gb + "?\n\n"
                            + "The game will close. Open it again to play.")
                    .setCancelable(false)
                    .setPositiveButton("Delete file", (d, w) -> {
                        try { DocumentsContract.deleteDocument(a.getContentResolver(), uri); }
                        catch (Throwable t) { Log.w(TAG, "delete failed", t); }
                        quit();
                    })
                    .setNegativeButton("Keep file", (d, w) -> quit())
                    .show();
        }

        void quit() {
            Activity a = getActivity();
            if (a != null) a.finishAffinity();
            android.os.Process.killProcess(android.os.Process.myPid());
        }

        void progress(final long done, final long total, final String name) {
            final Activity a = getActivity();
            if (a == null) return;
            a.runOnUiThread(() -> {
                if (bar == null) return;
                if (total > 0) bar.setProgress((int) (done * 1000 / total));
                text.setText(String.format(java.util.Locale.US, "%.2f / %.2f GB\n%s",
                        done / 1073741824.0, total / 1073741824.0, name));
            });
        }

        /** Streams a ustar/GNU tar: header blocks of 512 bytes, data padded to 512. */
        void unpack(Activity a, Uri uri, long total) throws IOException {
            File files = filesDir(a), obb = a.getObbDir();
            if (files == null || obb == null) throw new IOException("storage is not available");
            files.mkdirs(); obb.mkdirs();
            long free = new StatFs(files.getPath()).getAvailableBytes();
            if (total > 0 && free < total + 200L * 1024 * 1024)
                throw new IOException(String.format(java.util.Locale.US,
                        "not enough free space (%.1f GB free, %.1f GB needed)", free / 1073741824.0, total / 1073741824.0));
            new File(files, MARKER).delete();
            byte[] hdr = new byte[512];
            byte[] buf = new byte[1 << 20];
            long done = 0, lastUi = 0;
            boolean sawMagic = false;
            String longName = null;
            try (InputStream in = new BufferedInputStream(a.getContentResolver().openInputStream(uri), 1 << 20)) {
                while (true) {
                    if (!readFully(in, hdr, 512)) break;
                    done += 512;
                    if (isZero(hdr)) break;
                    String name = cstr(hdr, 0, 100);
                    String prefix = cstr(hdr, 345, 155);
                    if (!prefix.isEmpty()) name = prefix + "/" + name;
                    long size = octal(hdr, 124, 12);
                    char type = (char) hdr[156];
                    if (longName != null) { name = longName; longName = null; }
                    if (type == 'L') {                       // GNU long name: next data block holds the name
                        byte[] nb = new byte[(int) size];
                        readFully(in, nb, nb.length);
                        skip(in, pad(size));
                        done += size + pad(size);
                        longName = new String(nb, StandardCharsets.UTF_8).replace("\0", "");
                        continue;
                    }
                    while (name.startsWith("./")) name = name.substring(2);
                    if (!sawMagic) {
                        if (!name.equals(MAGIC)) throw new IOException("this is not a Girl Cafe Gun data file");
                        sawMagic = true;
                    }
                    File target = null;
                    if (name.startsWith("obb/")) target = new File(obb, name.substring(4));
                    else if (name.startsWith("files/")) target = new File(files, name.substring(6));
                    if (target != null && name.contains("..")) throw new IOException("bad entry " + name);
                    if (type == '5' || name.endsWith("/")) {
                        if (target != null) target.mkdirs();
                    } else if (type == '0' || type == 0) {
                        OutputStream out = null;
                        if (target != null) {
                            target.getParentFile().mkdirs();
                            out = new FileOutputStream(target);
                        }
                        long left = size;
                        try {
                            while (left > 0) {
                                int n = in.read(buf, 0, (int) Math.min(buf.length, left));
                                if (n < 0) throw new IOException("data file is incomplete (download not finished?)");
                                if (out != null) out.write(buf, 0, n);
                                left -= n; done += n;
                                if (done - lastUi > (8 << 20)) { lastUi = done; progress(done, total, name); }
                            }
                        } finally { if (out != null) out.close(); }
                        skip(in, pad(size));
                        done += pad(size);
                    } else {
                        skip(in, size + pad(size));
                        done += size + pad(size);
                    }
                }
            }
            if (!sawMagic) throw new IOException("this is not a Girl Cafe Gun data file");
            try (OutputStream m = new FileOutputStream(new File(files, MARKER))) {
                m.write(("installed " + System.currentTimeMillis()).getBytes(StandardCharsets.UTF_8));
            }
            progress(total, total, "done");
        }
    }

    static long size(ContentResolver cr, Uri uri) {
        try (Cursor c = cr.query(uri, new String[]{OpenableColumns.SIZE}, null, null, null)) {
            if (c != null && c.moveToFirst() && !c.isNull(0)) return c.getLong(0);
        } catch (Throwable ignored) {}
        return -1;
    }

    static long pad(long size) { return (512 - (size % 512)) % 512; }

    static boolean readFully(InputStream in, byte[] b, int len) throws IOException {
        int off = 0;
        while (off < len) {
            int n = in.read(b, off, len - off);
            if (n < 0) { if (off == 0) return false; throw new IOException("data file is incomplete (download not finished?)"); }
            off += n;
        }
        return true;
    }

    static void skip(InputStream in, long n) throws IOException {
        byte[] t = new byte[8192];
        while (n > 0) {
            int r = in.read(t, 0, (int) Math.min(t.length, n));
            if (r < 0) throw new IOException("data file is incomplete (download not finished?)");
            n -= r;
        }
    }

    static boolean isZero(byte[] b) { for (byte x : b) if (x != 0) return false; return true; }

    static String cstr(byte[] b, int off, int len) {
        int end = off;
        while (end < off + len && b[end] != 0) end++;
        return new String(b, off, end - off, StandardCharsets.UTF_8);
    }

    /** Octal size field; GNU base-256 for files over 8 GB (high bit set). */
    static long octal(byte[] b, int off, int len) {
        if ((b[off] & 0x80) != 0) {
            long v = 0;
            for (int i = off + 1; i < off + len; i++) v = (v << 8) | (b[i] & 0xff);
            return v;
        }
        long v = 0;
        for (int i = off; i < off + len; i++) {
            int c = b[i];
            if (c == 0 || c == ' ') { if (v != 0 || c == 0) { if (c == 0) break; continue; } continue; }
            if (c < '0' || c > '7') break;
            v = (v << 3) + (c - '0');
        }
        return v;
    }

    @SuppressWarnings("unused") static int unused = Color.BLACK;
}
