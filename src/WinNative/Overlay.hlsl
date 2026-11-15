cbuffer Scene : register(b0) {
    float2 size; float time; float scale;
    float2 orb; float radius; float orbOpacity;
    float energy; float mood; float edgeOpacity; float flash;
    float blur; float glassOpacity; float glow; float palette;
};

struct Quad { float4 position : SV_POSITION; };
Quad VS(uint id : SV_VertexID) {
    Quad o;
    o.position = float4(id == 2 ? 3 : -1, id == 1 ? -3 : 1, 0, 1);
    return o;
}

float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
float noise(float2 p) {
    float2 i = floor(p), f = frac(p);
    f = f*f*(3-2*f);
    return lerp(lerp(hash(i),hash(i+float2(1,0)),f.x),lerp(hash(i+float2(0,1)),hash(i+1),f.x),f.y);
}
float3 spectrum(float u) {
    float3 a = float3(.20,.51,1), b = float3(.64,.24,1), c = float3(.21,.91,.94), d = float3(1,.34,.69);
    u = frac(u + palette) * 4;
    float t = smoothstep(0,1,frac(u));
    return u < 1 ? lerp(a,b,t) : u < 2 ? lerp(b,d,t) : u < 3 ? lerp(d,c,t) : lerp(c,a,t);
}
float4 layer(float3 color, float alpha) { alpha = saturate(alpha); return float4(saturate(color)*alpha,alpha); }
float4 atop(float4 top,float4 bottom) { return top+bottom*(1-top.a); }
float capsule(float2 p,float w,float h) { return length(float2(max(abs(p.x)-(w-h)*.5,0),p.y))-h*.5; }

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

float4 Orb(float2 xy) {
    if (orbOpacity<.002 || radius<1) return 0;
    float2 p = (xy-orb)/radius;
    float r = length(p);
    if (r>1.35) return 0;
    float aa = 1.4/radius;
    float mask = 1-smoothstep(1-aa,1+aa,r);
    float t = time;
    float4 halo = layer(lerp(spectrum(atan2(p.y,p.x)/6.283+t*.13),float3(.98,.19,.30),step(4.5,mood)*.75),
        exp(-abs(r-1)*17)*(.15+.11*energy+.18*flash)*orbOpacity*glow);
    if (mask<.001) return halo;
    float z = sqrt(saturate(1-dot(p,p)));
    float3 n = float3(p,z);
    float2 q = p*(1+.17*(1-z)) + float2(z*.14,-z*.11);
    float f = noise(q*3.7+float2(t*.25,-t*.39));
    float g = noise(q*3.4+float2(-t*.33,t*.26)+float2(f,1-f));
    float seam = sin(q.x*4.2+q.y*4-t*1.8+(f-g)*4);
    float ribbon = exp(-pow((seam+.15)*2.7,2)) * (.22+.43*f);
    float clouds = smoothstep(.21,.81,noise(q*5+float2(g-t*.31,f+t*.21)));
    float3 liquid = spectrum(f*.48+g*.28+t*.065);
    float3 body = float3(.073,.103,.24) + liquid*(.15+.37*clouds+(.56-blur*.19)*ribbon);
    float rim = pow(1-z,2.7);
    float spec = pow(saturate(dot(n,normalize(float3(-.42,-.66,1)))),32-blur*12);
    float reflect = exp(-pow((length((p-float2(-.15,-.24))*float2(1,1.45))-.59)*20,2));
    body += float3(.68,.81,1)*(.63*spec+.11*reflect)+spectrum(g+t*.08)*rim*.59;
    body += (hash(xy)-.5)*.036;
    body = lerp(body,body*.55+float3(.50,.045,.12),step(4.5,mood)*.79);

    float microphone = capsule(float2(p.y+.045,p.x),.20,.10);
    float pickup = max(abs(length(float2(p.x,p.y+.015))-.092)-.009,-(p.y+.013));
    float stand = max(abs(p.x)-.008,max(.071-p.y,p.y-.125));
    float icon = 1-smoothstep(.002,.017,min(microphone,min(pickup,stand)));
    float waveMask = smoothstep(.14,.31,abs(p.x))*(1-smoothstep(.64,.80,abs(p.x)));
    float wave = sin((p.x*17+t*5))*sin(p.x*6-t*2.3)*(.018+.20*energy)*waveMask;
    float waveLine = (1-smoothstep(.012,.041,abs(p.y-wave))) * waveMask;
    float waveEcho = exp(-abs(p.y-wave*.66-.058)*32)*waveMask;
    float luminous = (icon*.92+waveLine*.71+waveEcho*.34)*(.65+energy*.35);
    body = lerp(body,float3(.88,.96,1),saturate(luminous));
    float opacity = (.52+.19*clouds+.22*rim+.17*spec+.3*luminous)*glassOpacity;
    return atop(layer(body,mask*orbOpacity*opacity),halo);
}

float4 PS(Quad input) : SV_TARGET {
    float2 xy = input.position.xy;
    return atop(Orb(xy),Edge(xy));
}

struct Particle { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR0; };
Particle ParticleVS(uint id : SV_VertexID,uint count : SV_InstanceID) {
    Particle result;
    float seed = float(count)+1;
    float random = hash(float2(seed,87));
    float life = frac(random+time*(.17+hash(float2(seed,33))*.25));
    float phase = seed*2.399963 + time*(.56+energy*.61);
    float2 direction = float2(cos(phase),sin(phase));
    float distance = sqrt(hash(float2(seed,14)))*.80;
    float2 flow = float2(-direction.y,direction.x)*sin(time*1.9+seed)*.12;
    float2 p = (direction * distance + flow + direction * energy * sin(life*6.283)*.09);
    float2 center = orb+radius*p;
    float2 delta = float2(id == 1 || id == 3 ? 1 : -1,id >= 2 ? 1 : -1);
    float2 tangent = float2(-direction.y,direction.x);
    float particleSize = scale*(1.2+random*2.4+energy*1.4);
    float alpha = sin(life*3.14159)*orbOpacity*(.48+.39*energy)*saturate((.94-length(p))/.18);
    if (count < 12 && flash > .042) {
        float heading = seed*.523599 + time*.33;
        direction = float2(cos(heading),sin(heading));
        tangent = direction;
        float2 end = ((step(0,direction)*2-1)*.5)*size+size*.5;
        float progress = pow(saturate(1-flash),.7);
        center = lerp(end,orb,progress)+float2(-direction.y,direction.x)*sin(progress*3.14159)*size.y*.06;
        alpha = flash*progress*.57;
        particleSize *= 1.22;
    }
    center += (tangent*delta.x*(1.7+energy)+direction*delta.y)*particleSize;
    result.position = float4(center/size*float2(2,-2)+float2(-1,1),0,1);
    result.uv = delta;
    result.color = float4(spectrum(random+time*.1),alpha);
    return result;
}

float4 ParticlePS(Particle input) : SV_TARGET {
    float core = exp(-dot(input.uv,input.uv)*4.2);
    return layer(lerp(input.color.rgb,float3(.76,.94,1),core*.58),core*input.color.a);
}
