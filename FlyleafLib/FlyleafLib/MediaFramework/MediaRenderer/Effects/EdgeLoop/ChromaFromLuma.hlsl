// Chroma from luma: rebuilds full-resolution colour for 4:2:0 video (NV12, P010) from its full-resolution
// brightness. Written for EdgeLoop (GPL-3.0); the idea is AV1's chroma-from-luma prediction.
//
// Each thread takes one colour sample. Over its 3x3 neighbourhood it fits a straight line (least squares)
// between brightness (each colour sample's 2x2 brightness pixels averaged) and each colour channel. Its four
// brightness pixels then get their colour from that line, kept within the neighbourhood's range, weighted by
// how well the line explains the colour there (the squared correlation): edges where colour follows
// brightness come out sharp, and anything else keeps the usual bilinear blend.

cbuffer Sizes : register(b0) {
	uint2 lumaSize;
	uint2 chromaSize;
	float2 lumaPt;
	float2 chromaPt;
};

Texture2DArray<float> Luma : register(t0);     // The decoder's views are one-slice arrays
Texture2DArray<float2> Chroma : register(t1);
RWTexture2D<float2> Output : register(u0);     // Colour at brightness resolution
SamplerState LinearClamp : register(s0);

// Below about half an 8-bit step, differences are rounding, not detail
static const float Noise = (0.5f / 255) * (0.5f / 255);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
	const uint2 c = id.xy;
	if (c.x >= chromaSize.x || c.y >= chromaSize.y) {
		return;
	}

	float sumL = 0, sumLL = 0;
	float2 sumC = 0, sumCC = 0, sumLC = 0;
	float2 low = 1e9f, high = -1e9f;
	[unroll]
	for (int dy = -1; dy <= 1; ++dy) {
		[unroll]
		for (int dx = -1; dx <= 1; ++dx) {
			const int2 n = clamp(int2(c) + int2(dx, dy), int2(0, 0), int2(chromaSize) - 1);
			// Sampled at the colour sample's centre, the brightness is its 2x2 pixels' average
			const float l = Luma.SampleLevel(LinearClamp, float3((n + 0.5f) * chromaPt, 0), 0);
			const float2 ch = Chroma.Load(int4(n, 0, 0));
			sumL += l;
			sumLL += l * l;
			sumC += ch;
			sumCC += ch * ch;
			sumLC += l * ch;
			low = min(low, ch);
			high = max(high, ch);
		}
	}

	const float meanL = sumL / 9;
	const float2 meanC = sumC / 9;
	const float varL = max(sumLL / 9 - meanL * meanL, 0);
	const float2 varC = max(sumCC / 9 - meanC * meanC, 0);
	const float2 cov = sumLC / 9 - meanL * meanC;

	const float2 alpha = cov / (varL + Noise);
	const float2 beta = meanC - alpha * meanL;
	const float2 fit = saturate(cov * cov / ((varL + Noise) * (varC + Noise)));

	[unroll]
	for (uint j = 0; j < 2; ++j) {
		[unroll]
		for (uint i = 0; i < 2; ++i) {
			const uint2 p = c * 2 + uint2(i, j);
			if (p.x >= lumaSize.x || p.y >= lumaSize.y) {
				continue;
			}

			const float l = Luma.Load(int4(p, 0, 0));
			const float2 blended = Chroma.SampleLevel(LinearClamp, float3((p + 0.5f) * lumaPt, 0), 0);
			const float2 predicted = clamp(alpha * l + beta, low, high);
			Output[p] = lerp(blended, predicted, fit);
		}
	}
}
