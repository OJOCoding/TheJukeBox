// Generates the PWA icon set.
//
//   node tools/generate-icons.mjs
//
// Writes PNGs with a hand-rolled encoder over node:zlib, so this needs no packages and no
// image library. That is not cleverness for its own sake: the frontend deliberately has no
// npm dependencies, and adding a build-time image toolchain purely to draw two squares
// would be the largest dependency in the project.
//
// The mark is the design language reduced to its essentials: a filled square with the
// signature hard offset shadow and one L-shaped registration corner. Pure black and white,
// so it stays crisp at 16px and needs no anti-aliasing tricks.

import { deflateSync } from 'node:zlib';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const outputDir = join(here, '..', 'src', 'TheJukeBox', 'wwwroot', 'icons');

const BLACK = [0x00, 0x00, 0x00];
const WHITE = [0xff, 0xff, 0xff];

/** CRC-32, as PNG requires on every chunk. */
const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
});

function crc32(buffer) {
    let c = 0xffffffff;
    for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);

    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);

    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body));

    return Buffer.concat([length, body, crc]);
}

/**
 * Encodes RGB pixels as a PNG.
 *
 * Filter byte 0 (None) on every scanline. Adaptive filtering would compress better, but
 * these images are two flat colours and already a couple of kilobytes.
 */
function encodePng(width, height, pixelAt) {
    const raw = Buffer.alloc(height * (1 + width * 3));
    let offset = 0;

    for (let y = 0; y < height; y++) {
        raw[offset++] = 0;

        for (let x = 0; x < width; x++) {
            const [r, g, b] = pixelAt(x, y);
            raw[offset++] = r;
            raw[offset++] = g;
            raw[offset++] = b;
        }
    }

    const ihdr = Buffer.alloc(13);
    ihdr.writeUInt32BE(width, 0);
    ihdr.writeUInt32BE(height, 4);
    ihdr[8] = 8;  // bit depth
    ihdr[9] = 2;  // colour type 2 = truecolour RGB
    ihdr[10] = 0; // deflate
    ihdr[11] = 0; // adaptive filtering
    ihdr[12] = 0; // no interlace

    return Buffer.concat([
        Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
        chunk('IHDR', ihdr),
        chunk('IDAT', deflateSync(raw, { level: 9 })),
        chunk('IEND', Buffer.alloc(0)),
    ]);
}

/**
 * The mark.
 *
 * @param size    image dimension in pixels
 * @param inset   fraction of the image kept clear around the mark. Maskable icons need
 *                the art inside a safe zone, because the platform crops to its own shape
 *                (a circle on many Android launchers) and art drawn to the edges loses its
 *                corners.
 */
function mark(size, inset) {
    const pad = Math.round(size * inset);
    const box = size - pad * 2;

    // Proportional to the artwork, so the shadow reads the same at 192 and 512.
    const shadow = Math.max(2, Math.round(box * 0.14));
    const stroke = Math.max(1, Math.round(box * 0.06));
    const corner = Math.round(box * 0.26);

    const squareLeft = pad;
    const squareTop = pad;
    const squareRight = pad + box - shadow;
    const squareBottom = pad + box - shadow;

    return (x, y) => {
        const inSquare = x >= squareLeft && x < squareRight && y >= squareTop && y < squareBottom;

        // The offset shadow: the same square displaced down and right.
        const inShadow =
            x >= squareLeft + shadow && x < squareRight + shadow &&
            y >= squareTop + shadow && y < squareBottom + shadow;

        if (inSquare) {
            const nearLeft = x - squareLeft < stroke;
            const nearTop = y - squareTop < stroke;
            const nearRight = squareRight - x <= stroke;
            const nearBottom = squareBottom - y <= stroke;

            // Border.
            if (nearLeft || nearTop || nearRight || nearBottom) return BLACK;

            // The L-shaped registration corner, top-right, drawn as two rules. The same
            // motif the container pseudo-elements draw in the app itself.
            const inCornerBand = squareRight - x <= corner && y - squareTop <= corner;

            if (inCornerBand) {
                const onVertical = squareRight - x > corner - stroke;
                const onHorizontal = y - squareTop >= corner - stroke;
                if (onVertical || onHorizontal) return BLACK;
            }

            return WHITE;
        }

        if (inShadow) return BLACK;

        return WHITE;
    };
}

mkdirSync(outputDir, { recursive: true });

const targets = [
    // Standard icons are drawn nearly full-bleed.
    { name: 'icon-192.png', size: 192, inset: 0.08 },
    { name: 'icon-512.png', size: 512, inset: 0.08 },
    { name: 'apple-touch-icon-180.png', size: 180, inset: 0.08 },

    // Maskable art sits at roughly 66% so the platform's crop cannot clip it. Reusing the
    // full-bleed art here is the usual mistake and loses the corners.
    { name: 'icon-maskable-512.png', size: 512, inset: 0.19 },
];

for (const { name, size, inset } of targets) {
    const png = encodePng(size, size, mark(size, inset));
    writeFileSync(join(outputDir, name), png);
    console.log(`${name.padEnd(28)} ${size}x${size}  ${png.length} bytes`);
}

console.log(`\nWrote ${targets.length} icons to ${outputDir}`);
