#!/usr/bin/env python3
"""
Builds DeepVault's disposable test disk images using REAL file-system implementations
(ntfs-3g, mtools/dosfstools, exfat-fuse) and real deletions, then records SHA-256 hashes of
every file so the automated tests can prove byte-exact recovery.

Run as root on Linux with FUSE available:
    python3 make_samples.py /tmp/dv-samples
    python3 make_images.py /tmp/dv-samples ../../tests/fixtures/images

Never point this at a real disk: it only creates and edits files inside the output directory.
"""
import gzip, hashlib, json, os, random, shutil, struct, subprocess, sys, time, zlib

SAMPLES, OUT = sys.argv[1], sys.argv[2]
WORK = os.path.join(OUT, "_work")
os.makedirs(WORK, exist_ok=True)
MNT = os.path.join(WORK, "mnt")
os.makedirs(MNT, exist_ok=True)
rnd = random.Random(424242)
MiB = 1024 * 1024
manifest = {"generated_by": "tools/fixtures/make_images.py", "images": []}


def sh(*cmd, **kw):
    return subprocess.run(list(cmd), check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, **kw).stdout.decode(errors="replace")


def sha(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def sample(name):
    return os.path.join(SAMPLES, name)


def mkfile(path, size_mb):
    with open(path, "wb") as f:
        f.truncate(size_mb * MiB)


def mount_ntfs(img):
    sh("ntfs-3g", "-o", "streams_interface=xattr", img, MNT)


LOOP = None


def mount_exfat(img):
    # exfat-fuse requires a block device when run as root, so attach the image to a loop device.
    global LOOP
    LOOP = sh("losetup", "-f", "--show", img).strip()
    sh("mount.exfat-fuse", LOOP, MNT)


def umount():
    os.sync()
    for _ in range(20):
        try:
            sh("fusermount", "-u", MNT)
            global LOOP
            if LOOP:
                sh("losetup", "-d", LOOP)
                LOOP = None
            return
        except subprocess.CalledProcessError:
            time.sleep(0.3)
    raise RuntimeError("umount failed")


def mbr_disk(path, size_mb, parts):
    """parts: list of (start_sector, sectors, type_byte)."""
    mkfile(path, size_mb)
    mbr = bytearray(512)
    struct.pack_into("<I", mbr, 0x1B8, 0x44565431)
    for i, (start, count, t) in enumerate(parts):
        e = 0x1BE + i * 16
        mbr[e + 4] = t
        mbr[e + 1:e + 4] = b"\xfe\xff\xff"
        mbr[e + 5:e + 8] = b"\xfe\xff\xff"
        struct.pack_into("<II", mbr, e + 8, start, count)
    mbr[510:512] = b"\x55\xaa"
    with open(path, "r+b") as f:
        f.write(mbr)


def put(disk, offset, part_img):
    with open(disk, "r+b") as f, open(part_img, "rb") as p:
        f.seek(offset)
        shutil.copyfileobj(p, f, 4 * MiB)


def rec(lst, name, src, path, deleted=True, expect="exact", **extra):
    d = {"name": name, "path": path, "size": os.path.getsize(src), "sha256": sha(src), "deleted": deleted, "expect": expect}
    d.update(extra)
    lst.append(d)


def finish(name, disk, scenario, files, **extra):
    gz = os.path.join(OUT, name + ".img.gz")
    with open(disk, "rb") as f, open(gz, "wb") as gzf, gzip.GzipFile(fileobj=gzf, mode="wb", compresslevel=9, mtime=0, filename="") as g:
        shutil.copyfileobj(f, g, 4 * MiB)
    entry = {"name": name, "file": name + ".img.gz", "size": os.path.getsize(disk), "sha256": sha(disk), "scenario": scenario, "files": files}
    entry.update(extra)
    manifest["images"].append(entry)
    print(f"{name}: {os.path.getsize(disk)//MiB} MiB -> {os.path.getsize(gz)//1024} KiB gz, {len(files)} tracked files")


# ---------------------------------------------------------------- Test A: NTFS deleted files
def ntfs_deleted():
    disk, part = os.path.join(WORK, "ntfs_deleted.img"), os.path.join(WORK, "p.img")
    part_mb = 47
    mkfile(part, part_mb)
    sh("mkfs.ntfs", "-F", "-Q", "-q", "-p", "2048", "-L", "DVTEST", part)
    mount_ntfs(part)
    files = []
    for d in ["Photos/2019", "Documents", "Music", "Keep", "Compressed", "Filler"]:
        os.makedirs(os.path.join(MNT, d), exist_ok=True)
    plan = [("Photos/2019", "holiday_beach.jpg"), ("Photos/2019", "animation.gif"), ("Photos", "diagram.png"),
            ("Documents", "invoice_2019.pdf"), ("Documents", "thesis.docx"), ("Documents", "old_report.doc"),
            ("Documents", "tiny.txt"), ("Documents", "notes.txt"), ("Documents", "letter.rtf"),
            ("Music", "song.mp3"), ("Music", "memo.wav"), ("Music", "lossless.flac")]
    for d, n in plan:
        shutil.copy(sample(n), os.path.join(MNT, d, n))
    shutil.copy(sample("budget.xlsx"), os.path.join(MNT, "Keep", "budget.xlsx"))
    # NTFS compression (LZNT1) on a folder: files created inside are compressed by ntfs-3g.
    comp_src = os.path.join(WORK, "compressible.txt")
    open(comp_src, "w").write("".join(f"Compressible line {i % 50} of a long text document.\n" for i in range(6000)))
    sh("setfattr", "-n", "system.ntfs_attrib_be", "-v", "0x00000800", os.path.join(MNT, "Compressed"))
    shutil.copy(comp_src, os.path.join(MNT, "Compressed", "compressible.txt"))
    attrs = sh("getfattr", "-n", "system.ntfs_attrib_be", "-e", "hex", os.path.join(MNT, "Compressed", "compressible.txt"))
    compressed = "0x00000800" in attrs or int(attrs.split("=")[-1], 16) & 0x800 != 0
    # Fill the volume, punch holes, then write a large file so it MUST be fragmented.
    fillers = []
    i = 0
    while True:
        fp = os.path.join(MNT, "Filler", f"f{i:04d}.bin")
        try:
            with open(fp, "wb") as f:
                f.write(bytes([i & 0xFF]) * 49152)
            fillers.append(fp)
            i += 1
        except OSError:
            if os.path.exists(fp):
                os.remove(fp)
            break
    # ntfs-3g alternates allocations between regions, so free fillers i%4 in {0,1}: this leaves
    # holes of at most ~96 KiB everywhere, smaller than the 365 KiB file written next.
    punched = [fp for i, fp in enumerate(fillers) if i % 4 in (0, 1)]
    for fp in punched:
        os.remove(fp)
    shutil.copy(sample("family_large.jpg"), os.path.join(MNT, "Photos", "family_large.jpg"))
    os.sync()
    for fp in fillers:
        if fp not in punched:
            os.remove(fp)
    # Record and delete targets.
    for d, n in plan:
        rec(files, n, sample(n), "\\" + d.replace("/", "\\"), resident=(n == "tiny.txt"))
    rec(files, "family_large.jpg", sample("family_large.jpg"), "\\Photos", fragmented=True)
    rec(files, "compressible.txt", comp_src, "\\Compressed", ntfs_compressed=compressed)
    rec(files, "budget.xlsx", sample("budget.xlsx"), "\\Keep", deleted=False)
    for d, n in plan:
        os.remove(os.path.join(MNT, d, n))
    os.remove(os.path.join(MNT, "Photos", "family_large.jpg"))
    os.remove(os.path.join(MNT, "Compressed", "compressible.txt"))
    os.rmdir(os.path.join(MNT, "Photos", "2019"))  # deleted directory: paths must still resolve
    umount()
    mbr_disk(disk, 48, [(2048, part_mb * MiB // 512, 0x07)])
    put(disk, 2048 * 512, part)
    finish("ntfs_deleted", disk, "NTFS volume; files deleted through ntfs-3g (one fragmented, one LZNT1-compressed, one resident, one deleted directory)",
           files, partition_offset=2048 * 512, compressed_applied=compressed)
    os.remove(part)


# ---------------------------------------------------------------- Test A: FAT32 deleted files
def fat32_deleted():
    disk, part = os.path.join(WORK, "fat32_deleted.img"), os.path.join(WORK, "p.img")
    part_mb = 63
    mkfile(part, part_mb)
    sh("mkfs.vfat", "-F", "32", "-n", "DVFAT", "-s", "4", "-h", "2048", part)
    files = []
    M = ["-i", part]
    sh("mmd", *M, "::/Photos", "::/Docs", "::/Docs/Old Projects", "::/Keep")
    targets = [("Photos", "holiday_beach.jpg"), ("Photos", "phone_photo.webp"), ("Docs", "invoice_2019.pdf"),
               ("Docs", "contacts.csv"), ("Docs/Old Projects", "old_budget.xls"), ("Docs/Old Projects", "backup.zip"),
               ("Docs", "A long file name for testing LFN recovery.txt")]
    long_src = os.path.join(WORK, "lfn.txt")
    open(long_src, "w").write("Long file name test content.\n" * 300)
    for d, n in targets:
        src = long_src if n.startswith("A long") else sample(n)
        sh("mcopy", *M, src, f"::/{d}/{n}")
        rec(files, n, src, "\\" + d.replace("/", "\\"))
    sh("mcopy", *M, sample("slides.pptx"), "::/Keep/slides.pptx")
    rec(files, "slides.pptx", sample("slides.pptx"), "\\Keep", deleted=False)
    for d, n in targets[:5] + targets[6:]:
        sh("mdel", *M, f"::/{d}/{n}")
    sh("mdeltree", *M, "::/Docs/Old Projects")  # deletes backup.zip and the directory itself
    mbr_disk(disk, 64, [(2048, part_mb * MiB // 512, 0x0C)])
    put(disk, 2048 * 512, part)
    finish("fat32_deleted", disk, "FAT32 volume; files and a directory deleted with mtools (mdel/mdeltree)", files, partition_offset=2048 * 512)
    os.remove(part)


# ---------------------------------------------------------------- Test A: exFAT deleted files
def exfat_deleted():
    disk, part = os.path.join(WORK, "exfat_deleted.img"), os.path.join(WORK, "p.img")
    part_mb = 47
    mkfile(part, part_mb)
    sh("mkfs.exfat", "-L", "DVEXFAT", part)
    mount_exfat(part)
    files = []
    os.makedirs(os.path.join(MNT, "Camera"), exist_ok=True)
    for n in ["clip.mp4", "holiday_beach.jpg", "song.mp3", "invoice_2019.pdf"]:
        shutil.copy(sample(n), os.path.join(MNT, "Camera", n))
        rec(files, n, sample(n), "\\Camera")
    shutil.copy(sample("notes.txt"), os.path.join(MNT, "notes.txt"))
    rec(files, "notes.txt", sample("notes.txt"), "\\", deleted=False)
    os.sync()
    for n in ["clip.mp4", "holiday_beach.jpg", "song.mp3", "invoice_2019.pdf"]:
        os.remove(os.path.join(MNT, "Camera", n))
    umount()
    mbr_disk(disk, 48, [(2048, part_mb * MiB // 512, 0x07)])
    put(disk, 2048 * 512, part)
    finish("exfat_deleted", disk, "exFAT volume; files deleted through exfat-fuse", files, partition_offset=2048 * 512)
    os.remove(part)


# ---------------------------------------------------------------- Test B/D: raw carving
def carve_raw():
    disk = os.path.join(WORK, "carve_raw.img")
    size = 24 * MiB
    buf = bytearray(size)
    files = []
    pos = 64 * 1024
    intact = ["holiday_beach.jpg", "diagram.png", "animation.gif", "scan.bmp", "document_scan.tif", "phone_photo.webp",
              "invoice_2019.pdf", "thesis.docx", "budget.xlsx", "slides.pptx", "old_report.doc", "old_budget.xls",
              "backup.zip", "archive.7z", "logs.gz", "dump.xz", "app_data.sqlite", "letter.rtf", "song.mp3", "memo.wav",
              "lossless.flac", "voice.ogg", "clip.mp4", "camera.mov", "old_video.avi", "movie.mkv", "family_large.jpg",
              "settings.xml", "page.html"]

    def junk(n, zero=False):
        return bytes(n) if zero else bytes(rnd.getrandbits(8) for _ in range(n))

    def place(data):
        nonlocal pos
        off = pos
        buf[off:off + len(data)] = data
        end = off + len(data)
        gap = rnd.choice([0, 512, 4096, 9000, 20000])
        zero = rnd.random() < 0.5
        pad = (-end) % 512
        buf[end:end + pad] = junk(pad, zero)
        buf[end + pad:end + pad + gap] = junk(gap, zero)
        pos = end + pad + gap
        pos += (-pos) % 512
        return off

    for n in intact:
        data = open(sample(n), "rb").read()
        off = place(data)
        rec(files, n, sample(n), None, expect="exact", offset=off)
    # Truncated PNG: only the first 40% survives, followed by random data.
    png = open(sample("diagram.png"), "rb").read()
    off = place(png[: len(png) * 4 // 10] + junk(30000))
    files.append({"name": "truncated.png", "offset": off, "expect": "damaged", "size": len(png), "sha256": hashlib.sha256(png).hexdigest()})
    # Fragmented PNG: two pieces separated by 64 KiB of foreign data.
    png2 = open(sample("screenshot_big.png"), "rb").read()
    cut = 300 * 1024
    off = place(png2[:cut] + junk(64 * 1024) + png2[cut:])
    files.append({"name": "fragmented.png", "offset": off, "expect": "damaged", "size": len(png2), "sha256": hashlib.sha256(png2).hexdigest()})
    # JPEG whose header was overwritten.
    jpg = bytearray(open(sample("holiday_beach.jpg"), "rb").read())
    jpg[2:40] = junk(38)
    off = place(bytes(jpg))
    files.append({"name": "corrupt_header.jpg", "offset": off, "expect": "not_verified", "size": len(jpg)})
    # Truncated ZIP (central directory lost).
    z = open(sample("backup.zip"), "rb").read()
    off = place(z[: len(z) // 2] + junk(8192))
    files.append({"name": "truncated.zip", "offset": off, "expect": "damaged", "size": len(z)})
    assert pos < size
    open(disk, "wb").write(buf)
    finish("carve_raw", disk, "No file system: files at sector-aligned offsets separated by random/zero gaps, plus truncated/fragmented/corrupt files", files)


# ---------------------------------------------------------------- Test C: lost partitions
def lost_partitions():
    disk = os.path.join(WORK, "lost_partitions.img")
    total_mb = 96
    mkfile(disk, total_mb)
    sh("sgdisk", "-Z", disk)
    sh("sgdisk", "-n", "1:2048:+40M", "-t", "1:0700", "-c", "1:Data", "-n", "2:0:+40M", "-t", "2:0700", "-c", "2:Media", disk)
    info = sh("sgdisk", "-i", "1", disk) + sh("sgdisk", "-i", "2", disk)
    starts = [int(l.split(":")[1].split()[0]) for l in info.splitlines() if l.startswith("First sector")]
    ends = [int(l.split(":")[1].split()[0]) for l in info.splitlines() if l.startswith("Last sector")]
    files = []
    # Partition 1: NTFS
    p1 = os.path.join(WORK, "p1.img")
    with open(p1, "wb") as f:
        f.truncate((ends[0] - starts[0] + 1) * 512)
    sh("mkfs.ntfs", "-F", "-Q", "-q", "-p", str(starts[0]), "-L", "LOSTNTFS", p1)
    mount_ntfs(p1)
    os.makedirs(os.path.join(MNT, "Work"), exist_ok=True)
    for n in ["thesis.docx", "invoice_2019.pdf", "notes.txt"]:
        shutil.copy(sample(n), os.path.join(MNT, "Work", n))
        rec(files, n, sample(n), "\\Work", deleted=False, partition=1)
    umount()
    put(disk, starts[0] * 512, p1)
    # Partition 2: FAT32
    p2 = os.path.join(WORK, "p2.img")
    with open(p2, "wb") as f:
        f.truncate((ends[1] - starts[1] + 1) * 512)
    sh("mkfs.vfat", "-F", "32", "-n", "LOSTFAT", "-h", str(starts[1]), p2)
    sh("mmd", "-i", p2, "::/Pictures")
    for n in ["holiday_beach.jpg", "diagram.png"]:
        sh("mcopy", "-i", p2, sample(n), f"::/Pictures/{n}")
        rec(files, n, sample(n), "\\Pictures", deleted=False, partition=2)
    put(disk, starts[1] * 512, p2)
    os.remove(p1)
    os.remove(p2)
    # Destroy the partition tables (protective MBR, primary and backup GPT) and the NTFS primary boot sector.
    sectors = os.path.getsize(disk) // 512
    with open(disk, "r+b") as f:
        f.seek(0); f.write(bytes(512 * 34))
        f.seek((sectors - 33) * 512); f.write(bytes(512 * 33))
        f.seek(starts[0] * 512); f.write(bytes(512))
    parts = [{"index": 1, "offset": starts[0] * 512, "length": (ends[0] - starts[0] + 1) * 512, "fs": "NTFS", "note": "primary boot sector wiped; only backup boot sector remains"},
             {"index": 2, "offset": starts[1] * 512, "length": (ends[1] - starts[1] + 1) * 512, "fs": "FAT32"}]
    finish("lost_partitions", disk, "GPT disk whose MBR, primary GPT, backup GPT and NTFS primary boot sector were wiped", files, partitions=parts)


# ---------------------------------------------------------------- Reformat ("Windows reinstall") scenario
def reformatted():
    disk, part = os.path.join(WORK, "reformatted_ntfs.img"), os.path.join(WORK, "p.img")
    part_mb = 63
    mkfile(part, part_mb)
    sh("mkfs.ntfs", "-F", "-Q", "-q", "-p", "2048", "-L", "OLDWIN", part)
    mount_ntfs(part)
    files = []
    base = os.path.join(MNT, "Users", "Owner")
    for d in ["Documents", "Pictures", "Music", "Documents/Taxes"]:
        os.makedirs(os.path.join(base, d), exist_ok=True)
    # Many small files grow the old MFT well beyond what a fresh format initialises.
    for i in range(400):
        sub = "Documents" if i % 2 else "Documents/Taxes"
        txt = os.path.join(base, sub, f"letter_{i:03d}.txt")
        with open(txt, "w") as f:
            f.write(f"Letter {i}\n" + ("Lorem ipsum dolor sit amet. " * (20 + i % 60)))
    old = [("Pictures", "holiday_beach.jpg"), ("Pictures", "diagram.png"), ("Documents", "invoice_2019.pdf"),
           ("Documents", "thesis.docx"), ("Music", "song.mp3"), ("Documents", "old_report.doc")]
    for d, n in old:
        shutil.copy(sample(n), os.path.join(base, d, n))
        rec(files, n, sample(n), "\\Users\\Owner\\" + d, expect="best_effort")
    for i in (7, 150, 399):
        sub = "Documents" if i % 2 else "Documents\\Taxes"
        src = os.path.join(base, sub.replace("\\", "/"), f"letter_{i:03d}.txt")
        cp = os.path.join(WORK, f"letter_{i:03d}.txt")
        shutil.copy(src, cp)
        rec(files, f"letter_{i:03d}.txt", cp, "\\Users\\Owner\\" + sub, expect="best_effort")
    umount()
    # "Reinstall": quick-format the same partition and write a few new files.
    sh("mkfs.ntfs", "-F", "-Q", "-q", "-p", "2048", "-L", "NEWWIN", part)
    mount_ntfs(part)
    os.makedirs(os.path.join(MNT, "Windows", "System32"), exist_ok=True)
    for i in range(20):
        with open(os.path.join(MNT, "Windows", "System32", f"sys{i:02d}.dat"), "wb") as f:
            f.write(bytes([0x5A]) * (4096 * (1 + i % 3)))
    umount()
    mbr_disk(disk, 64, [(2048, part_mb * MiB // 512, 0x07)])
    put(disk, 2048 * 512, part)
    finish("reformatted_ntfs", disk, "NTFS volume populated, then quick-formatted again and partially reused (simulates a Windows reinstall)",
           files, partition_offset=2048 * 512)
    os.remove(part)


ntfs_deleted()
fat32_deleted()
exfat_deleted()
carve_raw()
lost_partitions()
reformatted()
json.dump(manifest, open(os.path.join(OUT, "manifest.json"), "w"), indent=1)
shutil.rmtree(WORK)
print("manifest written")
