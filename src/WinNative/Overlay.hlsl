cbuffer Scene : register(b0) {
    float2 size; float time; float scale;
    float energy; float edgeOpacity; float flash; float glow;
    float palette; float3 reserved;
};

struct Quad { float4 position : SV_POSITION; };
Quad VS(uint id : SV_VertexID) {
    Quad o;
    o.position = float4(id == 2 ? 3 : -1, id == 1 ? -3 : 1, 0, 1);
    return o;
}

float4 layer(float3 color, float alpha) { alpha = saturate(alpha); return float4(saturate(color)*alpha,alpha); }

// Distance along the screen border, clockwise from the top-right corner, in pixels.
float Perimeter(float2 xy) {
    float w = size.x, h = size.y;
    float2 m = min(xy, size-xy);
    if (m.x < m.y) return xy.x > w*.5 ? xy.y : (2*h + w - xy.y);
    return xy.y > h*.5 ? (h + w - xy.x) : (2*h + w + xy.x);
}

float3 EdgeSpectrum(float u) {
    u = frac(u + palette) * 6;
    float t = smoothstep(0, 1, frac(u));
    float3 pink = float3(1, .13, .48), coral = float3(1, .28, .16);
    float3 gold = float3(1, .68, .10), mint = float3(.05, .94, .58);
    float3 cyan = float3(.04, .68, 1), violet = float3(.55, .21, 1);
    if (u < 1) return lerp(pink, coral, t);
    if (u < 2) return lerp(coral, gold, t);
    if (u < 3) return lerp(gold, mint, t);
    if (u < 4) return lerp(mint, cyan, t);
    if (u < 5) return lerp(cyan, violet, t);
    return lerp(violet, pink, t);
}

float4 Edge(float2 xy) {
    float2 margins = min(xy,size-xy)/scale;
    float dist = min(margins.x,margins.y);
    float spread = 17+12*energy+28*flash;
    if (dist>spread*4.4 || edgeOpacity+flash<.002) return 0;
    float along = Perimeter(xy)/(2*(size.x+size.y));
    // Breathe at fixed points along the frame. The hue coordinate never moves with time.
    float accents = .5 + .5*cos(along*18.849556);
    float breath = .88 + .10*cos(time*1.48) + .06*accents*cos(time*2.30);
    float rim = exp(-dist/2.9);
    float ribbon = exp(-dist/8.5);
    float halo = exp(-dist/spread) * (1-smoothstep(spread*3.4,spread*4.4,dist));
    float a = (rim*.37 + ribbon*.35 + halo*.29) * (breath+.18*energy)*edgeOpacity;
    a += flash*exp(-dist/26)*.49;
    float3 main = EdgeSpectrum(along);
    float3 fringe = EdgeSpectrum(along+.045);
    float3 color = lerp(main, fringe, .27*(1-ribbon));
    color = lerp(color, float3(1,.96,1), rim*.20);
    return layer(lerp(color,float3(.81,.87,1),flash*.24),a*glow);
}

float4 PS(Quad input) : SV_TARGET {
    return Edge(input.position.xy);
}