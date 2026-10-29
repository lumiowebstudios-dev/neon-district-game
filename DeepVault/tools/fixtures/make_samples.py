#!/usr/bin/env python3
"""
Generates DeepVault's synthetic test sample files (all content is generated here, so it is
freely redistributable; no personal data is involved).

Usage: python3 make_samples.py <output-dir>
Requires: Pillow, ffmpeg (libx264, libmp3lame, libvorbis, flac), 7z.
"""
import gzip, io, lzma, os, random, sqlite3, struct, subprocess, sys, wave, zipfile, math

OUT = sys.argv[1] if len(sys.argv) > 1 else "samples"
os.makedirs(OUT, exist_ok=True)
rnd = random.Random(20261029)


def p(name):
    return os.path.join(OUT, name)


def photo(w, h, seed):
    from PIL import Image, ImageDraw
    r = random.Random(seed)
    img = Image.new("RGB", (w, h))
    px = img.load()
    a, b, c = r.random() * 6, r.random() * 6, r.random() * 6
    for y in range(h):
        for x in range(w):
            px[x, y] = (
                int(127 + 120 * math.sin(x / (9 + a) + y / 23)) ^ r.randint(0, 24),
                int(127 + 120 * math.sin(y / (7 + b))) ^ r.randint(0, 24),
                int(127 + 120 * math.cos((x + y) / (11 + c))) ^ r.randint(0, 24),
            )
    d = ImageDraw.Draw(img)
    for _ in range(6):
        x0, y0 = r.randint(0, w - 20), r.randint(0, h - 20)
        d.ellipse([x0, y0, x0 + r.randint(10, 60), y0 + r.randint(10, 60)], outline=(255, 255, 255), width=2)
    return img


def images():
    photo(320, 240, 1).save(p("holiday_beach.jpg"), quality=88)
    photo(1024, 768, 2).save(p("family_large.jpg"), quality=92)  # ~300 KB, used for fragmentation tests
    photo(200, 150, 3).save(p("diagram.png"), optimize=False)
    photo(640, 480, 33).save(p("screenshot_big.png"), optimize=False)
    photo(120, 90, 4).convert("P").save(p("animation.gif"))
    photo(100, 80, 5).save(p("scan.bmp"))
    photo(160, 120, 6).save(p("document_scan.tif"), compression="raw")
    photo(160, 120, 7).save(p("phone_photo.webp"), quality=80)


def pdf():
    objs = [
        b"<< /Type /Catalog /Pages 2 0 R >>",
        b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        None,
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    text = b"BT /F1 24 Tf 72 700 Td (DeepVault test document - invoice 2019) Tj ET\n" + b"".join(
        b"BT /F1 10 Tf 72 %d Td (Line %d of synthetic content for recovery testing) Tj ET\n" % (680 - i * 12, i) for i in range(40))
    objs[3] = b"<< /Length %d >>\nstream\n" % len(text) + text + b"\nendstream"
    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offs = []
    for i, o in enumerate(objs, 1):
        offs.append(len(out))
        out += b"%d 0 obj\n" % i + o + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)
    for o in offs:
        out += b"%010d 00000 n \n" % o
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)
    open(p("invoice_2019.pdf"), "wb").write(out)


def ooxml(path, kind):
    ct = {
        "docx": ("word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
                 '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>' +
                 "".join(f"<w:p><w:r><w:t>Paragraph {i}: the quick brown fox jumps over the lazy dog.</w:t></w:r></w:p>" for i in range(200)) +
                 "</w:body></w:document>"),
        "xlsx": ("xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
                 '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheets><sheet name="Budget" sheetId="1"/></sheets></workbook>'),
        "pptx": ("ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml",
                 '<p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"/>'),
    }[kind]
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml",
                   '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                   '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                   '<Default Extension="xml" ContentType="application/xml"/>'
                   f'<Override PartName="/{ct[0]}" ContentType="{ct[1]}"/></Types>')
        z.writestr("_rels/.rels",
                   '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                   f'<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="{ct[0]}"/></Relationships>')
        z.writestr(ct[0], ct[2])


def ole2(path, streams):
    """Minimal but valid Compound File Binary (v3, 512-byte sectors). Streams must be >= 4096 bytes."""
    SS = 512
    ENDC, FREE, FATSECT, NOS = 0xFFFFFFFE, 0xFFFFFFFF, 0xFFFFFFFD, 0xFFFFFFFF
    fat = [FATSECT, ENDC]  # sector 0 = FAT, sector 1 = directory
    data = bytearray()
    starts = []
    next_sec = 2
    for name, content in streams:
        assert len(content) >= 4096
        n = (len(content) + SS - 1) // SS
        starts.append(next_sec)
        for i in range(n):
            fat.append(next_sec + i + 1 if i < n - 1 else ENDC)
        next_sec += n
        data += content + b"\0" * (n * SS - len(content))
    assert len(fat) <= 128
    fat += [FREE] * (128 - len(fat))

    def dirent(name, typ, left, right, child, start, size):
        nm = name.encode("utf-16-le") + b"\0\0"
        e = nm.ljust(64, b"\0") + struct.pack("<HBBIII", len(nm), typ, 1, left, right, child)
        e += b"\0" * 16 + struct.pack("<I", 0) + b"\0" * 16 + struct.pack("<IIi", start, size, 0)
        return e

    ents = [dirent("Root Entry", 5, NOS, NOS, 1, ENDC, 0)]
    for i, (name, content) in enumerate(streams):
        right = i + 2 if i + 1 < len(streams) else NOS
        ents.append(dirent(name, 2, NOS, right, NOS, starts[i], len(content)))
    while len(ents) < 4:
        ents.append(b"\0" * 64 + struct.pack("<HBBIII", 0, 0, 0, NOS, NOS, NOS) + b"\0" * 48)
    assert all(len(e) == 128 for e in ents)
    hdr = bytes.fromhex("D0CF11E0A1B11AE1") + b"\0" * 16 + struct.pack("<HHHHH", 0x3E, 3, 0xFFFE, 9, 6) + b"\0" * 6
    hdr += struct.pack("<IIIIIIIII", 0, 1, 1, 0, 4096, ENDC, 0, ENDC, 0)
    hdr += struct.pack("<I", 0) + struct.pack("<I", FREE) * 108
    assert len(hdr) == 512
    out = hdr + struct.pack("<128I", *fat) + b"".join(ents) + bytes(data)
    open(path, "wb").write(out)


def office_legacy():
    body = ("Legacy Word document body. " * 400).encode("utf-16-le")[:9000]
    ole2(p("old_report.doc"), [("WordDocument", body), ("1Table", bytes(range(256)) * 20)])
    ole2(p("old_budget.xls"), [("Workbook", bytes((i * 7) & 0xFF for i in range(6000)))])


def text_files():
    open(p("notes.txt"), "w").write("".join(f"Note {i}: remember to back up the photos folder.\n" for i in range(300)))
    open(p("tiny.txt"), "w").write("small resident file\n")
    open(p("contacts.csv"), "w").write("name,phone\n" + "".join(f"Person {i},555-01{i:02d}\n" for i in range(80)))
    open(p("page.html"), "w").write("<!DOCTYPE html>\n<html><head><title>Saved page</title></head><body>" + "<p>content</p>\n" * 200 + "</body></html>\n")
    open(p("letter.rtf"), "w").write(r"{\rtf1\ansi\deff0 {\fonttbl {\f0 Times New Roman;}}" + "\n" + "".join(r"\f0\fs24 Dear friend, line %d \{braces\}.\par" % i + "\n" for i in range(60)) + "}")
    open(p("settings.xml"), "w").write('<?xml version="1.0" encoding="utf-8"?>\n<settings>\n' + "".join(f'  <item key="k{i}" value="v{i}"/>\n' for i in range(150)) + "</settings>\n")


def archives():
    with zipfile.ZipFile(p("backup.zip"), "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("a.txt", "alpha\n" * 500)
        z.writestr("dir/b.bin", bytes(rnd.getrandbits(8) for _ in range(3000)))
    payload = ("compressible log line\n" * 2000).encode() + bytes(rnd.getrandbits(8) for _ in range(5000))
    open(p("logs.gz"), "wb").write(gzip.compress(payload, mtime=0))
    open(p("dump.xz"), "wb").write(lzma.compress(payload))
    src = p("_7zsrc.txt")
    open(src, "wb").write(payload)
    subprocess.run(["7z", "a", "-bd", "-y", p("archive.7z"), src], check=True, stdout=subprocess.DEVNULL)
    os.remove(src)


def database():
    path = p("app_data.sqlite")
    if os.path.exists(path):
        os.remove(path)
    con = sqlite3.connect(path)
    con.execute("create table t(id integer primary key, v text)")
    con.executemany("insert into t(v) values(?)", [(f"row {i} " * 5,) for i in range(500)])
    con.commit()
    con.close()


def media():
    def ff(*args):
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", *args], check=True)
    ff("-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:a", "libmp3lame", "-b:a", "64k", "-id3v2_version", "3", "-metadata", "title=Test tone", p("song.mp3"))
    ff("-f", "lavfi", "-i", "sine=frequency=660:duration=1", "-c:a", "flac", p("lossless.flac"))
    ff("-f", "lavfi", "-i", "sine=frequency=550:duration=1", "-c:a", "libvorbis", p("voice.ogg"))
    ff("-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", p("clip.mp4"))
    ff("-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", p("camera.mov"))
    ff("-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1", "-c:v", "mpeg4", p("old_video.avi"))
    ff("-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=1", "-c:v", "mpeg4", p("movie.mkv"))
    with wave.open(p("memo.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(8000)
        w.writeframes(b"".join(struct.pack("<h", int(8000 * math.sin(i / 10))) for i in range(8000)))


images(); pdf(); ooxml(p("thesis.docx"), "docx"); ooxml(p("budget.xlsx"), "xlsx"); ooxml(p("slides.pptx"), "pptx")
office_legacy(); text_files(); archives(); database(); media()
print("samples:", len(os.listdir(OUT)))
