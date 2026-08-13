//!HOOK OUTPUT
//!BIND HOOKED
//!DESC Contrast Adaptive Sharpening (AMD FidelityFX CAS)

// Sharpens AFTER the 1.33x upscale to 1440p, which is where the softness comes
// from. CAS is contrast-adaptive: it sharpens flat/soft areas more and already
// high-contrast edges less, so it recovers detail without the halos a plain
// unsharp mask produces. Hooks OUTPUT so it runs at screen resolution.
// One pass, 9 taps, GPU-side - no measurable latency cost.

// 0.6 rather than 0.5: the default capture is now the MJPEG pin, which costs
// ~10% fine edge detail vs raw (measured). CAS is contrast-adaptive so it
// recovers that without haloing. Toggle with x to compare; lower it here if
// it ever looks crunchy.
#define SHARPNESS 0.6   // 0 = subtle, 1 = maximum

vec4 hook() {
    vec2 p  = HOOKED_pos;
    vec2 ts = HOOKED_pt;

    vec3 a = HOOKED_tex(p + vec2(-ts.x, -ts.y)).rgb;
    vec3 b = HOOKED_tex(p + vec2(  0.0, -ts.y)).rgb;
    vec3 c = HOOKED_tex(p + vec2( ts.x, -ts.y)).rgb;
    vec3 d = HOOKED_tex(p + vec2(-ts.x,   0.0)).rgb;
    vec3 e = HOOKED_tex(p).rgb;
    vec3 f = HOOKED_tex(p + vec2( ts.x,   0.0)).rgb;
    vec3 g = HOOKED_tex(p + vec2(-ts.x,  ts.y)).rgb;
    vec3 h = HOOKED_tex(p + vec2(  0.0,  ts.y)).rgb;
    vec3 i = HOOKED_tex(p + vec2( ts.x,  ts.y)).rgb;

    // Soft min/max of the cross, then widen with the diagonals.
    vec3 mnRGB = min(min(min(d, e), min(f, b)), h);
    mnRGB += min(mnRGB, min(min(a, c), min(g, i)));

    vec3 mxRGB = max(max(max(d, e), max(f, b)), h);
    mxRGB += max(mxRGB, max(max(a, c), max(g, i)));

    // Guard the reciprocal: mxRGB is 0 on pure black, which would blow up.
    vec3 rcpM = vec3(1.0) / max(mxRGB, vec3(1e-5));
    vec3 amp  = sqrt(clamp(min(mnRGB, 2.0 - mxRGB) * rcpM, 0.0, 1.0));

    float peak = -1.0 / mix(8.0, 5.0, clamp(SHARPNESS, 0.0, 1.0));
    vec3 w     = amp * peak;
    vec3 rcpW  = vec3(1.0) / (1.0 + 4.0 * w);

    return vec4(clamp((b * w + d * w + f * w + h * w + e) * rcpW, 0.0, 1.0), 1.0);
}
