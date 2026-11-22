# Magpie shader effects (fork)

These files are unchanged copies of shader effects from [Magpie](https://github.com/Blinue/Magpie)
(`src/Effects`, commit `e213fdaf3774fc4bd40c391f56f55845200a4a3a`), which is GPL-3.0. They're embedded in
FlyleafLib and read at run time by `MagpieEffect` and `MagpieEffectRunner`, a C# port of Magpie's effect
compiler and drawer.

| File | What it does | Original work and licence |
|------|--------------|---------------------------|
| `FSRCNNX.hlsl` | Doubles a picture (FSRCNNX_x2_8-0-4-1) | [igv/FSRCNN-TensorFlow](https://github.com/igv/FSRCNN-TensorFlow), GPL-3.0 |
| `FSRCNNX_LineArt.hlsl` | Doubles line art, anime and drawings | igv/FSRCNN-TensorFlow, GPL-3.0 |
| `FSR_EASU.hlsl` | AMD FidelityFX FSR 1 upscaling, any scale | [GPUOpen-Effects/FidelityFX-FSR](https://github.com/GPUOpen-Effects/FidelityFX-FSR), MIT |
| `FSR_RCAS.hlsl` | AMD FidelityFX FSR 1 sharpening | GPUOpen-Effects/FidelityFX-FSR, MIT |
| `SSimDownscaler.hlsl` | Perception-based shrinking | [igv's SSimDownscaler](https://gist.github.com/igv/36508af3ffc84410fe39761d6969be10), LGPL-3.0 |

`../EdgeLoop/Deband.hlsl` is EdgeLoop's own effect in the same format (GPL-3.0), embedded the same way.

To update one, copy the new file from Magpie over it and run the tests (`MagpieEffectTests`): they read and
compile every bundled effect and check the upscalers' output on the GPU.
