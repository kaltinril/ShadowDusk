/*
 * sdw_vkd3d_wrapper.c — ShadowDusk's thin C wrapper around vkd3d-shader for the
 * WebAssembly build (Phase 4.1: vkd3d-shader 1.17 → WASM via emscripten 3.1.34).
 *
 * WHY THIS EXISTS: in the browser there is no P/Invoke — .NET WASM talks to native
 * code through [JSImport] into an emscripten module. This wrapper flattens the
 * chained vkd3d_shader_compile_info / vkd3d_shader_hlsl_source_info structs into a
 * single C call with scalar/pointer arguments, so the JS shim (and the C#
 * [JSImport] side) never has to build C structs on the WASM heap.
 *
 * THE SAME COMPILER, NEVER A SUBSTITUTE: this links the SAME pinned vkd3d-shader
 * 1.17 (WineHQ tarball, SHA-256-verified) that the desktop DXBC/FNA backends use
 * (src/ShadowDusk.HLSL/Vkd3d/*). The semantics below deliberately mirror the
 * desktop P/Invoke in Vkd3dShaderCompiler.cs:
 *   - source is RAW UTF-8 BYTES + length (NOT null-terminated),
 *   - entry_point / profile / source_name are C strings (UTF-8, NUL-terminated),
 *   - log_level = VKD3D_SHADER_LOG_WARNING (non-fatal diagnostics surface too,
 *     constraint 5: fail loudly),
 *   - no compile options (options = NULL, option_count = 0),
 *   - messages are surfaced VERBATIM, never reformatted here.
 *
 * ===========================================================================
 * ABI CONTRACT — DO NOT CHANGE SIGNATURES without recording the change in
 * plan/DONE/PHASE-4.1-SPIKE-wasm-directx-dxbc.md. The C# [JSImport] interop side is
 * written against exactly this surface.
 *
 *   int  sdw_vkd3d_compile(const unsigned char* source, int source_len,
 *                          const char* entry_point, const char* profile,
 *                          const char* source_name, int target_type,
 *                          unsigned char** out_code, int* out_size,
 *                          char** out_messages);
 *        Returns 0 (VKD3D_OK) on success, negative vkd3d_result on failure.
 *        target_type uses vkd3d's OWN enum values:
 *          4 = VKD3D_SHADER_TARGET_D3D_BYTECODE (SM1–3 token stream, FNA fx_2_0)
 *          5 = VKD3D_SHADER_TARGET_DXBC_TPF     (SM4/5 DXBC, MonoGame DX11)
 *        On success *out_code/*out_size hold the compiled blob (caller frees via
 *        sdw_vkd3d_free_code). *out_messages (may be set on success AND failure,
 *        or left NULL) carries vkd3d's verbatim diagnostics (caller frees via
 *        sdw_vkd3d_free_messages).
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

SDW_EXPORT
int sdw_vkd3d_compile(const unsigned char *source, int source_len,
                      const char *entry_point, const char *profile,
                      const char *source_name, int target_type,
                      unsigned char **out_code, int *out_size,
                      char **out_messages)
{
    struct vkd3d_shader_hlsl_source_info hlsl_info;
    struct vkd3d_shader_compile_info info;
    struct vkd3d_shader_code out;
    char *messages = NULL;
    int rc;

    /* Defensive defaults so the caller never reads garbage on failure. */
    if (out_code)
        *out_code = NULL;
    if (out_size)
        *out_size = 0;
    if (out_messages)
        *out_messages = NULL;

    if (!source || source_len < 0 || !out_code || !out_size)
        return VKD3D_ERROR_INVALID_ARGUMENT;

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
    info.options = NULL;
    info.option_count = 0;
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
