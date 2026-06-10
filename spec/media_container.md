# NBMS Media Container `.nbmg`

Version: draft 0.1

`.nbmg` is the optional NBMS media archive for BGA, images, video clips, layer images, POOR images, banners, stage files, and future visual assets.

## Container

Physical format: ZIP.

Required entries:

```text
manifest.json
media/
  asset files...
```

## Manifest

`manifest.json` uses the following base shape:

```json
{
  "format": "NBMS-MEDIA",
  "version": "0.1.0",
  "entries": [
    {
      "mediaId": "bga_intro",
      "path": "media/bga_intro.mp4",
      "type": "video",
      "mimeType": "video/mp4",
      "hash": "sha256-...",
      "width": 1920,
      "height": 1080,
      "durationMs": 32000
    }
  ]
}
```

## Entry Fields

- `mediaId`: stable ID referenced from chart `mediaEvents[]`.
- `path`: relative path inside `.nbmg`.
- `type`: `image`, `video`, `layer`, `poor`, `banner`, `stagefile`, or `other`.
- `mimeType`: optional MIME hint.
- `hash`: SHA-256 hash of the original asset bytes.
- `width`, `height`, `durationMs`: optional metadata. Readers must not require these fields.
- `rightsId`: optional reference to header `rights.entries[]`.

## Chart Events

Charts refer to media assets through `mediaEvents[]`.

```json
{
  "tick": 3840,
  "mediaId": "bga_intro",
  "type": "video",
  "layer": 0
}
```

`mediaEvents[]` is chart timing data. `.nbmg` is asset storage. Editors should keep these concepts separate.

## NBMS Native Policy

NBMS does not preserve BMS `#BMPxx`, `#BGAxx`, `#LAYER`, or `#POOR` as primary internal identifiers. During import, those IDs should be mapped to stable `mediaId` values and optional `nbms.bmsCompat` metadata.

