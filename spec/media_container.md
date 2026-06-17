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

## Visual Event State

`nbms.visual` v0.2 uses `mediaEvents[]` as an event stream. Players expand that stream into per-layer visual state during playback. Images and videos are treated as the same kind of layer state; the asset `type` in the `.nbmg` manifest decides whether the layer is decoded as an image or a video.

Recommended object form:

```json
{
  "tick": 3840,
  "type": "bga",
  "mediaId": "bga_intro",
  "layer": 0,
  "order": 0,
  "startOffsetMs": 0,
  "playbackRate": 1.0,
  "loop": false,
  "endBehavior": "holdLastFrame",
  "opacity": 1.0,
  "blend": "normal"
}
```

Fields:

- `type`: `bga`, `layer`, `poor`, `clear`, `banner`, `stagefile`, or `preview`. For prototype compatibility, readers treat `image` / `video` as `bga`.
- `mediaId`: media entry to display. Omitted for `clear`.
- `layer`: composition layer. Defaults are `0` for `bga` / `poor`, and `1` for `layer`.
- `order`: optional stable order for events at the same tick/layer, useful when importing BMS source order.
- `startOffsetMs`: video start offset. Default is `0`.
- `playbackRate`: playback rate for the video asset itself. Default is `1.0`. Viewer Hi-Speed and future chart/audio speed changes do not implicitly change it.
- `loop`: whether the video loops. Default is `false`.
- `endBehavior`: behavior after a video reaches its end. Default is `holdLastFrame`.
- `opacity`: layer opacity. Default is `1.0`.
- `blend`: default is `normal`; other blend modes are future extensions.

### Event Types

| type | role | default layer | mediaId | hash relevance |
| --- | --- | ---: | --- | --- |
| `bga` | Base timeline visual. BMS `#xxx04` maps here. | `0` | required | visual hash / optional score hash |
| `layer` | Overlay visual above base BGA. BMS `#xxx07` maps here. | `1` | required | visual hash / optional score hash |
| `poor` | POOR/miss visual source. BMS `#xxx06` maps here. Preview viewers may show it on timeline; gameplay viewers may trigger it on miss. | `0` | required | visual hash / optional score hash |
| `clear` | Clear a layer or all layers. | `null` means all layers | omitted | visual hash / optional score hash |
| `stagefile` | Song select / loading still image. | not timeline composited | required | package metadata |
| `banner` | Song list banner image. | not timeline composited | required | package metadata |
| `preview` | Preview media for song selection. | not timeline composited | required | package metadata |

### Defaults

| field | default | notes |
| --- | --- | --- |
| `layer` | `0` for `bga` / `poor`, `1` for `layer`, omitted for global `clear` | Larger layer draws above smaller layer. |
| `order` | `0` | Used only to stabilize same tick/layer/type events. |
| `startOffsetMs` | `0` | Applies to video assets. |
| `playbackRate` | `1.0` | Does not follow Viewer Hi-Speed or chart/audio practice speed. |
| `loop` | `false` | Applies to video assets. |
| `endBehavior` | `holdLastFrame` | Authors should place `clear` when the layer must disappear. |
| `opacity` | `1.0` | Range is `0.0` to `1.0`. |
| `blend` | `normal` | Other blend modes are future extensions. |

In compact-json chart files, `mediaEvents` may store optional visual fields in the fifth `extra` object:

```json
[[3840, 0, "bga", 0, { "order": 0, "playbackRate": 1.0, "endBehavior": "holdLastFrame" }]]
```

Event application order is stable:

1. `tick` ascending.
2. `layer` ascending. A `clear` event without `layer` applies before layer-specific events at the same tick.
3. `type` priority: `clear`, `bga`, `layer`, `poor`, `banner`, `stagefile`, `preview`.
4. `order` ascending.
5. `mediaId` ordinal ascending.

`clear` has no `mediaId`. When `layer` is present, it clears only that layer. When `layer` is omitted, it clears all visual layers.

The default video end behavior is `holdLastFrame`. This prevents unintended black frames after short movie clips. Authors should place an explicit `clear` event when the visual layer must disappear.

Video BGA playback uses the decoder's normal media clock by default. Viewer Hi-Speed affects only chart object spacing. Future chart/audio playback speed changes must not implicitly alter video speed; players should align video position on start/seek from the media event time and the requested playback start time. Use explicit `playbackRate` only when the video asset itself should be intentionally retimed.

## Validator Issue Codes

Visual validators should use the following issue codes:

| code | severity | target reference | meaning |
| --- | --- | --- | --- |
| `NBMS_VISUAL_MEDIA_REF_MISSING` | Error | `chart:{chartId}:media:{mediaId}` | `mediaEvents[]` references a missing `.nbmg` entry. |
| `NBMS_VISUAL_TYPE_UNKNOWN` | Warning | `chart:{chartId}:tick:{tick}:media:{mediaId}` | Event `type` is unknown. Readers may ignore it. |
| `NBMS_VISUAL_LAYER_INVALID` | Warning | `chart:{chartId}:tick:{tick}:media:{mediaId}` | Layer is outside the supported range for the viewer/editor. |
| `NBMS_VISUAL_CLEAR_WITH_MEDIA` | Warning | `chart:{chartId}:tick:{tick}` | `clear` event carries `mediaId`; readers should ignore the media reference. |
| `NBMS_VISUAL_MEDIA_KIND_UNSUPPORTED` | Warning | `media:{mediaId}` | `.nbmg` entry exists but the reader cannot decode its kind/codec. |

## NBMS Native Policy

NBMS does not preserve BMS `#BMPxx`, `#BGAxx`, `#LAYER`, or `#POOR` as primary internal identifiers. During import, those IDs should be mapped to stable `mediaId` values and optional `nbms.bmsCompat` metadata.
