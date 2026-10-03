/*
 * sdw_vkd3d_wrapper.c — ShadowDusk's thin C wrapper around vkd3d-shader for the
 * WebAssembly build (Phase 4.1: vkd3d-shader → WASM via emscripten 3.1.34).
 *
 * WHY THIS EXISTS: in the browser there is no P/Invoke — .NET WASM talks to native
 * code through [JSImport] into an emscripten module. This wrapper flattens the
 * chained vkd3d_shader_compile_info / vkd3d_shader_hlsl_source_info structs into a
 * single C call with scalar/pointer arguments, so the JS shim (and the C#
 * [JSImport] side) never has to build C structs on the WASM heap.
 *
 * THE SAME COMPILER, NEVER A SUBSTITUTE: this links the SAME pinned vkd3d-shader
 * (WineHQ tarball, SHA-256-verified) that the desktop DXBC/FNA backends use
 * (src/ShadowDusk.HLSL/Vkd3d/*). The semantics below deliberately mirror the
 * desktop P/Invoke in Vkd3dShaderCompiler.cs:
 *   - source is RAW UTF-8 BYTES + length (NOT null-terminated),
 *   - entry_point / profile / source_name are C strings (UTF-8, NUL-terminated),
 *   - log_level = VKD3D_SHADER_LOG_WARNING (non-fatal diagnostics surface too,
 *     constraint 5: fail loudly),
 *   - the compile options are the CALLER'S, passed through untouched (see below),
 *   - messages are surfaced VERBATIM, never reformatted here.
 *
 * COMPILE OPTIONS ARE NEVER DECIDED HERE (issue #295). Which
 * vkd3d_shader_compile_option values a target gets is chosen in ONE place, the
 * managed Vkd3dCompileContract.ResolveCompileOptions, which the desktop backend
 * marshals straight into vkd3d_shader_compile_info and the browser backend sends
 * through the JS shim to this wrapper. This wrapper used to pass options = NULL
 * while the desktop passed BACKWARD_COMPATIBILITY = MAP_SEMANTIC_NAMES for
 * DXBC_TPF, so the browser compiled SM3-semantic shaders differently from the
 * desktop (or refused them). Do not hard-code, add, drop or default an option
 * in this file: a second copy of the list is exactly what drifted.
 *
 * ===========================================================================
 * ABI CONTRACT — DO NOT CHANGE SIGNATURES without recording the change in
 * plan/DONE/PHASE-4.1-SPIKE-wasm-directx-dxbc.md. The C# [JSImport] interop side is
 * written against exactly this surface.
 *
 *   int  sdw_vkd3d_compile_options(const unsigned char* source, int source_len,
 *                          const char* entry_point, const char* profile,
 *                          const char* source_name, int target_type,
 *                          const unsigned int* options, int option_count,
 *                          unsigned char** out_code, int* out_size,
 *                          char** out_messages);
 *        Returns 0 (VKD3D_OK) on success, negative vkd3d_result on failure.
 *        target_type uses vkd3d's OWN enum values:
 *          4 = VKD3D_SHADER_TARGET_D3D_BYTECODE (SM1–3 token stream, FNA fx_2_0)
 *          5 = VKD3D_SHADER_TARGET_DXBC_TPF     (SM4/5 DXBC, MonoGame DX11)
 *        options is option_count consecutive (name, value) pairs of 32-bit
 *        unsigned integers, i.e. 2 * option_count words: name is a
 *        vkd3d_shader_compile_option_name, value its unsigned int value. NULL is
 *        allowed only with option_count == 0. More than SDW_VKD3D_MAX_OPTIONS,
 *        a negative count, or a NULL list with a non-zero count is
 *        VKD3D_ERROR_INVALID_ARGUMENT (never a silent truncation).
 *        On success *out_code/*out_size hold the compiled blob (caller frees via
 *        sdw_vkd3d_free_code). *out_messages (may be set on success AND failure,
 *        or left NULL) carries vkd3d's verbatim diagnostics (caller frees via
 *        sdw_vkd3d_free_messages).
 *
 *        (Modules built before issue #295 exported sdw_vkd3d_compile instead: the
 *        same call without the two option parameters, always compiling with no
 *        options. It is gone on purpose, so no option-less entry point exists.)
 *
 *   void sdw_vkd3d_free_code(unsigned char* p);   // vkd3d_shader_free_shader_code
 *   void sdw_vkd3d_free_messages(char* p);        // vkd3d_shader_free_messages
 * ===========================================================================
 */

#include <limits.h>
#include <stddef.h>
#include <string.h>

#include <vkd3d_shader.h>

#ifdef __EMSCRIPTEN__
#include <emscripten.h>
#define SDW_EXPORT EMSCRIPTEN_KEEPALIVE
#else
#define SDW_EXPORT
#endif

/* Upper bound on the caller's option list. vkd3d 2.1 defines fewer than 16 option
 * names and a name appears at most once, so 16 is room to spare; a longer list is
 * refused, never truncated. */
#define SDW_VKD3D_MAX_OPTIONS 16

SDW_EXPORT
int sdw_vkd3d_compile_options(const unsigned char *source, int source_len,
                              const char *entry_point, const char *profile,
                              const char *source_name, int target_type,
                              const unsigned int *options, int option_count,
                              unsigned char **out_code, int *out_size,
                              char **out_messages)
{
    struct vkd3d_shader_compile_option compile_options[SDW_VKD3D_MAX_OPTIONS];
    struct vkd3d_shader_hlsl_source_info hlsl_info;
    struct vkd3d_shader_compile_info info;
    struct vkd3d_shader_code out;
    char *messages = NULL;
    int rc, i;

    /* Defensive defaults so the caller never reads garbage on failure. */
    if (out_code)
        *out_code = NULL;
    if (out_size)
        *out_size = 0;
    if (out_messages)
        *out_messages = NULL;

    if (!source || source_len < 0 || !out_code || !out_size)
        return VKD3D_ERROR_INVALID_ARGUMENT;

    /* The caller's option list, copied pair by pair into vkd3d's own struct (so the
     * ABI never depends on that struct's layout). Refused, not truncated or
     * defaulted, when it cannot be honoured exactly. */
    if (option_count < 0 || option_count > SDW_VKD3D_MAX_OPTIONS || (option_count > 0 && !options))
        return VKD3D_ERROR_INVALID_ARGUMENT;
    for (i = 0; i < option_count; ++i)
    {
        compile_options[i].name = (enum vkd3d_shader_compile_option_name)options[2 * i];
        compile_options[i].value = options[2 * i + 1];
    }

    /* Chained HLSL source info — mirrors Vkd3dHlslSourceInfo (Vkd3dNative.cs).
     * entry_point may be NULL (vkd3d defaults to "main"); profile is required by
     * vkd3d for HLSL input, but we pass whatever we got and let vkd3d emit its
     * own diagnostic (fail loudly, never pre-judge). */
    memset(&hlsl_info, 0, sizeof(hlsl_info));
    hlsl_info.type = VKD3D_SHADER_STRUCTURE_TYPE_HLSL_SOURCE_INFO;
    hlsl_info.next = NULL;
    hlsl_info.entry_point = entry_point;
    /* secondary_code stays {NULL, 0} via memset. */
    hlsl_info.profile = profile;

    /* Base compile info — mirrors Vkd3dCompileInfo (Vkd3dNative.cs). */
    memset(&info, 0, sizeof(info));
    info.type = VKD3D_SHADER_STRUCTURE_TYPE_COMPILE_INFO;
    info.next = &hlsl_info;
    info.source.code = source;          /* raw UTF-8 bytes, NOT null-terminated */
    info.source.size = (size_t)source_len;
    info.source_type = VKD3D_SHADER_SOURCE_HLSL;
    info.target_type = (enum vkd3d_shader_target_type)target_type;
    info.options = option_count ? compile_options : NULL;   /* the caller's, untouched */
    info.option_count = (unsigned int)option_count;
    info.log_level = VKD3D_SHADER_LOG_WARNING; /* warnings surface too (constraint 5) */
    info.source_name = source_name;     /* optional, may be NULL */

    memset(&out, 0, sizeof(out));
    rc = vkd3d_shader_compile(&info, &out, &messages);

    /* Messages surface VERBATIM on success and failure alike. If the caller did
     * not ask for them, free immediately (never leak the WASM heap). */
    if (out_messages)
        *out_messages = messages;
    else
        vkd3d_shader_free_messages(messages);

    if (rc != VKD3D_OK)
    {
        /* Defensive: vkd3d does not hand out code on failure, but if it ever
         * did, free it rather than leak. */
        if (out.code)
            vkd3d_shader_free_shader_code(&out);
        return rc;
    }

    if (out.size > (size_t)INT_MAX)
    {
        /* Cannot represent the size in the int-based ABI (cannot happen for real
         * shaders; wasm32 heaps are < 2 GiB anyway). Fail loudly, free the blob. */
        vkd3d_shader_free_shader_code(&out);
        return VKD3D_ERROR_OUT_OF_MEMORY;
    }

    *out_code = (unsigned char *)out.code;
    *out_size = (int)out.size;
    return VKD3D_OK;
}

SDW_EXPORT
void sdw_vkd3d_free_code(unsigned char *p)
{
    /* vkd3d_shader_free_shader_code() frees code->code only (the struct itself is
     * caller-owned) and ignores a NULL code pointer, so a {p, 0} shell is the
     * documented way to return the blob. */
    struct vkd3d_shader_code code;

    if (!p)
        return;
    code.code = p;
    code.size = 0;
    vkd3d_shader_free_shader_code(&code);
}

SDW_EXPORT
void sdw_vkd3d_free_messages(char *p)
{
    /* vkd3d_shader_free_messages() accepts NULL (no action). */
    vkd3d_shader_free_messages(p);
}
