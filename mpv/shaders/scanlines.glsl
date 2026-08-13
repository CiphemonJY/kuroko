//!HOOK OUTPUT
//!BIND HOOKED
//!DESC CRT scanlines

// Darkens every other output line for a CRT look. Hooks OUTPUT so the lines
// track the screen, not the source resolution.
vec4 hook() {
    vec4 c = HOOKED_tex(HOOKED_pos);
    float y = HOOKED_pos.y * HOOKED_size.y;
    c.rgb *= 0.62 + 0.38 * step(0.5, fract(y * 0.5));
    return c;
}
