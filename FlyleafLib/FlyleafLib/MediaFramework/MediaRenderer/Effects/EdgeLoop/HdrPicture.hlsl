// HDR output with the upscaling effects
// Written for EdgeLoop (GPL-3.0).
//
// The effects expect a picture whose values run from 0 to 1 and look evenly spaced, as SDR's do. With HDR output
// the renderer's picture is linear scRGB instead: BT.709 primaries, 1.0 = 80 nits, brighter than 1 in highlights
// and below 0 for colours outside BT.709. ToPQ turns it into PQ (SMPTE ST 2084) in BT.2020, which holds every one
// of those between 0 and 1, spaced the way the eye sees them; FromPQ turns the effects' result back into scRGB as
// it's drawn onto the screen. Both draw with VS, one triangle covering the target, and sample with sampler s0.

Texture2D Picture : register(t0);
SamplerState Sampler : register(s0);

struct PSInput
{
    float4 Position : SV_POSITION;
    float2 Texture  : TEXCOORD0;
};

PSInput VS(uint id : SV_VertexID)
{
    PSInput output;
    output.Texture  = float2((id << 1) & 2, id & 2);
    output.Position = float4(output.Texture * float2(2, -2) + float2(-1, 1), 0, 1);
    return output;
}

// SMPTE ST 2084
static const float M1 = 2610.0 / 16384.0;
static const float M2 = 2523.0 / 4096.0 * 128.0;
static const float C1 = 3424.0 / 4096.0;
static const float C2 = 2413.0 / 4096.0 * 32.0;
static const float C3 = 2392.0 / 4096.0 * 32.0;

// ITU-R BT.2087 (linear light)
static const float3x3 BT709To2020 =
{
    0.627404, 0.329283, 0.043313,
    0.069097, 0.919540, 0.011362,
    0.016391, 0.088013, 0.895595
};

static const float3x3 BT2020To709 =
{
     1.660491, -0.587641, -0.072850,
    -0.124550,  1.132900, -0.008349,
    -0.018151, -0.100579,  1.118730
};

float3 NitsToPQ(float3 nits)
{
    float3 y = pow(saturate(nits / 10000.0), M1);
    return pow((C1 + C2 * y) / (1.0 + C3 * y), M2);
}

float3 PQToNits(float3 pq)
{
    float3 p = pow(saturate(pq), 1.0 / M2);
    return pow(max(p - C1, 0.0) / (C2 - C3 * p), 1.0 / M1) * 10000.0;
}

// The renderer's scRGB picture (premultiplied) as PQ in BT.2020, straight alpha
float4 ToPQ(PSInput input) : SV_TARGET
{
    float4 c = Picture.Sample(Sampler, input.Texture);
    float3 nits = mul(BT709To2020, c.rgb / max(c.a, 1e-6)) * 80.0;
    return float4(NitsToPQ(nits), c.a);
}

// The effects' PQ picture back as scRGB, premultiplied as the swap chain wants it
float4 FromPQ(PSInput input) : SV_TARGET
{
    float4 c = Picture.Sample(Sampler, input.Texture);
    float3 scRGB = mul(BT2020To709, PQToNits(c.rgb)) / 80.0;
    return float4(scRGB * c.a, c.a);
}
