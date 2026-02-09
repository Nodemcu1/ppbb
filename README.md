# ppbb

## PaintballUltra ImageLibrary assets

The PaintballUltra UI uses ImageLibrary keys defined in `PaintballUltra.cs` (`ImageUrls` plus arena thumbnails).
Use these layout guidelines to prepare your images so they align with the panel anchors and preserve the
premium minimalist look.

| Asset key | Usage | Recommended size | Notes |
| --- | --- | --- | --- |
| `background` | Panel background image (all menus) | **1024×1024** (square) | Used as a full panel background. Keep a soft matte gradient with subtle texture. |
| `panel` | Arena voting thumbnail fallback | **512×610** | Matches the vote card area on a 16:9 screen (approx 0.26w × 0.55h). |
| `button` | Optional button texture/accent | **512×128** | Slim horizontal accent for buttons if you swap to image buttons. |
| `accent` | Optional accent strip | **512×64** | Thin highlight for separators or headers. |

### Arena thumbnails

Each arena can provide `Thumbnail` (ImageLibrary key or URL). Recommended aspect ratio: **~0.84** (e.g. **512×610**).
This aligns with the vote card slots (`AnchorMin/Max` in `ShowVotingUi`). Keep the same size across arenas for a
clean grid and avoid heavy contrast so the vote text remains readable.

### Layout targets (for 1920×1080 reference)

- **Main menu panel:** ~960×690 px (anchors 0.25–0.75 x 0.18–0.82)
- **Voting cards:** ~500×594 px (anchors width 0.26, height 0.55)
- **Lobby panel:** ~500×648 px (anchors 0.02–0.28 x 0.2–0.8)

These are guidelines; ImageLibrary will scale assets automatically, so consistency across assets is more important
than exact pixels.
