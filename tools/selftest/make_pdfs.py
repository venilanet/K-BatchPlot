"""生成 3 个结构标准的单页 PDF（经典 xref 表 + 内容流），供 BuiltInPdfMerge 实测。
不依赖任何第三方库；同时负责校验合并产物。"""
import os
import re
import sys


def make_pdf(path, text, width=595, height=842):
    objs = []

    def add(s):
        objs.append(s if isinstance(s, bytes) else s.encode('latin-1'))
        return len(objs)

    content = ("BT /F1 14 Tf 72 720 Td (%s) Tj ET\n" % text).encode('latin-1')
    # 1 Catalog / 2 Pages / 3 Page / 4 Contents / 5 Font
    add(b"<< /Type /Catalog /Pages 2 0 R >>")
    add(b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    add(("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] "
         "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>" % (width, height)))
    add(b"<< /Length " + str(len(content)).encode() + b" >>\nstream\n" + content + b"endstream")
    add(b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")

    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets = [0]
    for i, body in enumerate(objs, 1):
        offsets.append(len(out))
        out += ("%d 0 obj\n" % i).encode()
        out += body
        out += b"\nendobj\n"
    xref = len(out)
    out += ("xref\n0 %d\n" % (len(objs) + 1)).encode()
    out += b"0000000000 65535 f \r\n"
    for off in offsets[1:]:
        out += ("%010d 00000 n \r\n" % off).encode()
    out += ("trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n"
            % (len(objs) + 1, xref)).encode()
    with open(path, 'wb') as f:
        f.write(out)
    return len(out)


def verify_merged(path, expected_pages=3):
    data = open(path, 'rb').read()
    s = data.decode('latin-1')
    problems = []

    if not s.startswith('%PDF-'):
        problems.append('缺少 %PDF 头')
    pages = len(re.findall(r'/Type\s*/Page(?![sA-Za-z])', s))
    if pages != expected_pages:
        problems.append('/Type /Page 数量 = %d，期望 %d' % (pages, expected_pages))
    if not re.search(r'/Type\s*/Pages\s*/Count\s*%d' % expected_pages, s):
        problems.append('新页树根缺少 /Count %d' % expected_pages)
    for i in range(1, expected_pages + 1):
        if ('Page %d' % i).encode() not in data:
            problems.append('丢失第 %d 页的内容流' % i)

    m = re.search(r'startxref\s+(\d+)', s)
    if not m:
        problems.append('缺少 startxref')
    else:
        x = int(m.group(1))
        if not s[x:x + 4] == 'xref':
            problems.append('startxref 偏移不指向 xref 表')
        else:
            body = s[x + 4:]
            sm = re.match(r'\s*(\d+)\s+(\d+)\s*\r?\n', body)
            if not sm:
                problems.append('xref 子段头不可解析')
            else:
                cnt = int(sm.group(2))
                rows = re.findall(r'(\d{10})\s+(\d{5})\s+([nf])\s*\r?\n',
                                  body[sm.end():sm.end() + cnt * 21 + 20])
                if len(rows) < cnt:
                    problems.append('xref 条目不足：%d/%d' % (len(rows), cnt))
                else:
                    bad = 0
                    for idx, (off, gen, kind) in enumerate(rows):
                        if kind == 'f':
                            continue
                        pos = int(off)
                        if pos >= len(s) or not re.match(r'%d\s+0\s+obj' % idx, s[pos:pos + 30]):
                            bad += 1
                    if bad:
                        problems.append('xref 偏移错位的对象数 = %d' % bad)

    root = re.search(r'/Root\s+(\d+)\s+0\s+R', s[s.rfind('trailer'):])
    if not root:
        problems.append('trailer 缺少 /Root')
    if not s.rstrip().endswith('%%EOF'):
        problems.append('缺少 %%EOF')
    return len(data), pages, problems


if __name__ == '__main__':
    d = sys.argv[1]
    for i in range(1, 4):
        n = make_pdf(os.path.join(d, 'page%d.pdf' % i), 'Page %d' % i, 595 + i, 842)
        print('  generated page%d.pdf  %d bytes' % (i, n))
    merged = os.path.join(d, 'merged.pdf')
    if os.path.isfile(merged):
        size, pages, probs = verify_merged(merged)
        print('  merged.pdf  %d bytes  pages=%d' % (size, pages))
        if probs:
            for p in probs:
                print('  VERIFY FAIL  ' + p)
            sys.exit(2)
        print('  VERIFY PASS  xref 偏移/页树/内容流全部正确')
