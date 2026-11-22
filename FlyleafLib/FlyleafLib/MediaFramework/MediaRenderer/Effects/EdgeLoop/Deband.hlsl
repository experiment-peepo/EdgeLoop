// Deband: smooths the stair-step bands that compression leaves in gradients (skies, dark scenes).
// Written for EdgeLoop (GPL-3.0) in Magpie's effect format. The method is flash3kyuu_deband's: a pixel
// nearly the same as the average of a few neighbours in a random direction is part of a gradient, so it
// takes that average. Further neighbours have to be closer still, and a little noise hides what's left.

//!MAGPIE EFFECT
//!VERSION 4

//!PARAMETER
//!LABEL Threshold
//!DEFAULT 0.005
//!MIN 0
//!MAX 0.05
//!STEP 0.001
float threshold;

//!PARAMETER
//!LABEL Range
//!DEFAULT 16
//!MIN 1
//!MAX 32
//!STEP 1
float range;

//!PARAMETER
//!LABEL Iterations
//!DEFAULT 2
//!MIN 1
//!MAX 8
//!STEP 1
int iterations;

//!PARAMETER
//!LABEL Grain
//!DEFAULT 0.002
//!MIN 0
//!MAX 0.02
//!STEP 0.001
float grain;

//!TEXTURE
Texture2D INPUT;

//!TEXTURE
//!WIDTH INPUT_WIDTH
//!HEIGHT INPUT_HEIGHT
Texture2D OUTPUT;

//!SAMPLER
//!FILTER LINEAR
SamplerState linearSampler;

//!PASS 1
//!STYLE PS
//!IN INPUT
//!OUT OUTPUT

// PCG hash, from Jarzynski and Olano, "Hash Functions for GPU Rendering" (2020)
uint Hash(uint value) {
	uint state = value * 747796405u + 2891336453u;
	uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
	return (word >> 22u) ^ word;
}

// 0 to 1, moving the seed on
float Random(inout uint seed) {
	seed = Hash(seed);
	return (seed & 0xFFFFFFu) / 16777216.0f;
}

float4 Pass1(float2 pos) {
	const float2 pt = GetInputPt();
	const uint2 pixel = uint2(pos * GetInputSize());
	uint seed = Hash(pixel.x ^ Hash(pixel.y));

	float3 result = INPUT.SampleLevel(linearSampler, pos, 0).rgb;
	for (int i = 1; i <= iterations; ++i) {
		// Four neighbours a quarter turn apart, in a random direction, further out each time
		const float angle = Random(seed) * 6.2831853f;
		const float distance = Random(seed) * range * i / iterations;
		const float2 a = float2(cos(angle), sin(angle)) * distance * pt;
		const float2 b = float2(-a.y, a.x);
		const float3 average = 0.25f * (
			INPUT.SampleLevel(linearSampler, pos + a, 0).rgb + INPUT.SampleLevel(linearSampler, pos - a, 0).rgb +
			INPUT.SampleLevel(linearSampler, pos + b, 0).rgb + INPUT.SampleLevel(linearSampler, pos - b, 0).rgb);

		// Channels that close to the average are a gradient; anything further apart is detail and stays
		const float3 gradient = step(abs(result - average), threshold / i);
		result = lerp(result, average, gradient);
	}

	if (grain > 0) {
		result += (float3(Random(seed), Random(seed), Random(seed)) - 0.5f) * grain;
	}

	return float4(result, 1);
}
