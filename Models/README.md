# Detection models

Drop a YOLO11 `.onnx` detection model in this folder and it will be copied next
to `EdgeLoop.exe` at build time. `CensorModelLocator` picks it up automatically,
so `CensorModelPath` in settings can stay empty.

**Model files are not committed** — they are large, and they are not ours to
redistribute. See the licence note below.

## Which file

Detection runs on the **CPU** on purpose: the GPU is already busy decoding video
(and, if you use one, feeding a screen-capture censor), while the CPU usually has
headroom. So prefer the **fp32** builds — fp16 is a GPU optimisation and is
slower on CPU.

Preference order used by `CensorModelLocator`:

1. `hs-real-anime-y11n-320-fp32.onnx` — fastest, fine for full-screen video
2. `hs-real-y11n-320-fp32.onnx` — real footage only, no anime training data
3. `hs-real-anime-y11n-640-fp32.onnx` — better on small subjects, ~4× the compute
4. `hs-real-y11n-640-fp32.onnx`

If none of those names are present, the first `.onnx` in the folder is used.

Start at 320. Move to 640 only if small subjects are being missed — the input is
a whole video frame, so subjects are usually large in it.

## Where to get them

<https://huggingface.co/Perfectfox256/hotscreen-detection-models>

If you already run Hotscreen, the same files are in its `models/` folder.

## Class order

The 16 classes are read from the model's own embedded `names` metadata and
mirrored in `EdgeLoop/Classes/Censoring/Detection.cs`. The index order is what
the output tensor rows mean, so a model trained with a different class order
will silently censor the wrong things. `CensorDetectionTests` pins the order.

## Licence

The **y11** models are **AGPL-3.0** (trained with Ultralytics YOLOv11) and are
published for personal use. The **hs-yx** (YOLOX-derived) models are **MIT**.

EdgeLoop is GPL-3.0. Personal use of either is fine. If you ever redistribute a
build with a model bundled, the MIT `hs-yx` models are the clean choice — the
AGPL ones carry obligations you probably do not want to take on.
