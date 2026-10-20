// Make the game image blue. The green/red OSC markers must remain unchanged,
// and the translucent red marker must blend over blue exactly once.
void VS(uint id : SV_VertexID, out float4 pos : SV_Position)
{
    // Comparisons are supported by DX9 vs_3_0; integer bitwise operators are not.
    float2 uv = float2(id == 1 ? 2.0 : 0.0, id == 2 ? 2.0 : 0.0);
    pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}
float4 PS(float4 pos : SV_Position) : SV_Target
{
    return float4(0, 0, 1, 1);
}
technique Isolation
{
    pass { VertexShader = VS; PixelShader = PS; }
}
