#nullable enable

using ShadowDusk.Core.Reflection.Spirv;

namespace ShadowDusk.Core.Reflection;

/// <summary>
/// One (texture, sampler) pair that SPIRV-Cross's <c>build_combined_image_samplers</c> pass
/// collapses into a single GLSL combined-sampler uniform. Both names are the HLSL identifiers
/// (the SPIR-V <c>OpName</c> of the backing variables), which is the key BOTH reflection paths
/// agree on — the native DXIL oracle and the pure-managed <see cref="SpirvReflector"/> assign
/// different raw binding numbers, so a binding-keyed join would not be host-independent.
/// </summary>
/// <param name="TextureName">The HLSL name of the sampled texture (the image half of the pair).</param>
/// <param name="SamplerName">The HLSL name of the <c>SamplerState</c> (the sampler half).</param>
public sealed record CombinedSamplerPair(string TextureName, string SamplerName);

/// <summary>
/// Derives, in <b>exactly</b> the order SPIRV-Cross declares them, the combined-sampler uniforms
/// that the SPIR-V → GLSL transpile will emit — purely managed, straight from the SPIR-V words.
///
/// <para><b>Why this exists.</b> The MonoGame OpenGL runtime binds a texture unit to a sampler by
/// GLSL uniform NAME (<c>glGetUniformLocation("ps_s{k}")</c>), and <c>MonoGameGlslRewriter</c>
/// names those <c>ps_s{k}</c> in emitted-declaration order. SPIRV-Cross emits one combined
/// sampler per (texture, sampler) <i>pair</i>, so neither the reflected texture list nor the
/// reflected sampler list is the list the <c>.mgfx</c> sampler table has to mirror: N textures
/// read through one shared <c>SamplerState</c> is N uniforms but one reflected sampler, and the
/// mirror shape (one texture, N samplers) is N uniforms but one reflected texture. Keying the
/// table on either list silently binds the wrong texture (Phase 51 A7).</para>
///
/// <para><b>Why not just call SPIRV-Cross.</b> The pass already computes this list, but
/// <c>spvc_compiler_get_combined_image_samplers</c> is not one of the 11 functions
/// <c>src/ShadowDusk.Wasm/wwwroot/spirv-cross/spirv-cross.wasm</c> exports, and that module is an
/// out-of-band emscripten build. Reading it natively would fix the desktop path only and break
/// the CLI-vs-WASM byte-identity promise for this shape. Deriving it from the SPIR-V is
/// host-independent by construction, the same reason <c>RdefReader</c> and
/// <see cref="SpirvReflector"/> exist.</para>
///
/// <para><b>The ordering rule is transcribed, not guessed</b> (read from the pinned SPIRV-Cross
/// tree the WASM module is built from, tag <c>vulkan-sdk-1.4.335.0</c>):</para>
/// <list type="bullet">
///   <item><description><c>Compiler::build_combined_image_samplers</c> (<c>spirv_cross.cpp</c>)
///   runs <c>traverse_all_reachable_opcodes</c> from the single entry-point function: blocks in
///   binary order, ops in binary order, recursing into each <c>OpFunctionCall</c> target with a
///   parameter-to-argument remapping pushed for the callee's scope.</description></item>
///   <item><description>The only trigger is <c>OpSampledImage</c>. Its image and sampler operands
///   are resolved back to global variables through <c>remap_parameter</c> →
///   <c>maybe_get_backing_variable</c> (back through <c>OpLoad</c> / <c>OpAccessChain</c>), and
///   the pair is appended <b>if not already present</b>. So the order is <b>first-use order,
///   deduplicated</b> — unrelated to declaration order, bind slots, or binding numbers.</description></item>
///   <item><description>The synthesized variable ids come from <c>ir.increase_bound_by(2)</c>, so
///   they are monotonic in first-use order and sort after every original module id;
///   <c>CompilerGLSL::emit_resources</c> walks variables in id order and skips every separate
///   image/sampler when <c>vulkan_semantics</c> is off. Hence emitted declaration order IS
///   first-use pair order.</description></item>
/// </list>
///
/// <para><b>The emitted GLSL cannot be used instead.</b> SPIRV-Cross's readable
/// <c>SPIRV_Cross_Combined&lt;Image&gt;&lt;Sampler&gt;</c> name is applied by its command-line
/// tool, not by the pass, so through the C API the uniforms arrive as bare <c>_&lt;id&gt;</c>
/// (<c>uniform sampler2D _40;</c>) carrying no pair identity at all.</para>
///
/// <para>Every shape this model does not cover fails loudly with <c>SD0217</c> rather than
/// producing a plausible-but-wrong order, because a subtly wrong order is exactly the silent
/// mis-bind this class exists to eliminate.</para>
/// </summary>
public static class SpirvCombinedSamplerPairs
{
    /// <summary>
    /// Extracts the combined-sampler pairs in SPIRV-Cross declaration order. An empty list is a
    /// valid result (a shader that samples nothing).
    /// </summary>
    public static Result<IReadOnlyList<CombinedSamplerPair>, ShaderError> Extract(ReadOnlyMemory<byte> spirvBlob)
    {
        SpirvModule? module = SpirvModule.TryParse(spirvBlob.Span);
        if (module is null)
        {
            return Result<IReadOnlyList<CombinedSamplerPair>, ShaderError>.Fail(Error(
                "blob is not a valid SPIR-V module (bad magic or size)."));
        }

        try
        {
            return new Walker(module).Run();
        }
        catch (CombinedSamplerModelException ex)
        {
            return Result<IReadOnlyList<CombinedSamplerPair>, ShaderError>.Fail(Error(ex.Message));
        }
    }

    private static ShaderError Error(string message) => new(
        File:    "<spirv>",
        Line:    0,
        Column:  0,
        Code:    "SD0217",
        Message: "Cannot determine the OpenGL combined-sampler declaration order: " + message);

    /// <summary>Internal control flow for an input shape the ordering model does not cover.</summary>
    private sealed class CombinedSamplerModelException(string message) : Exception(message);

    private sealed class Walker
    {
        private readonly SpirvModule _module;

        // ---- Pass 1 tables -----------------------------------------------------
        private readonly Dictionary<uint, string> _names = new();

        /// <summary>OpTypePointer id -> pointee type id.</summary>
        private readonly Dictionary<uint, uint> _pointee = new();

        /// <summary>OpTypeImage ids whose Sampled operand is 1 (a separate, sampler-less image).</summary>
        private readonly HashSet<uint> _separateImageTypes = new();

        /// <summary>OpTypeSampler ids.</summary>
        private readonly HashSet<uint> _samplerTypes = new();

        /// <summary>OpTypeSampledImage ids (an already-combined sampler — not producible by DXC).</summary>
        private readonly HashSet<uint> _sampledImageTypes = new();

        /// <summary>Every OpVariable (module-level AND function-local), id -> declared type id.</summary>
        private readonly Dictionary<uint, uint> _variableTypes = new();

        /// <summary>Module-level OpVariables in binary order, id -> declared type id.</summary>
        private readonly List<(uint Id, uint TypeId)> _moduleVariables = new();

        private sealed class FunctionBody
        {
            public List<uint> Parameters { get; } = new();
            public List<SpirvModule.Instruction> Ops { get; } = new();
        }

        private readonly Dictionary<uint, FunctionBody> _functions = new();
        private uint _entryFunctionId;

        // ---- Pass 2 state ------------------------------------------------------

        /// <summary>
        /// Mirrors SPIRV-Cross's <c>SPIRExpression.loaded_from</c>: result id -> the backing
        /// variable an OpLoad / OpAccessChain of an image or sampler came from. Deliberately
        /// GLOBAL, not per-function-scope, matching <c>Compiler::register_read</c>.
        /// </summary>
        private readonly Dictionary<uint, uint> _loadedFrom = new();

        /// <summary>Mirrors <c>CombinedImageSamplerHandler::parameter_remapping</c>.</summary>
        private readonly Stack<Dictionary<uint, uint>> _remapping = new();

        private readonly List<(uint Image, uint Sampler)> _pairs = new();

        public Walker(SpirvModule module) => _module = module;

        public Result<IReadOnlyList<CombinedSamplerPair>, ShaderError> Run()
        {
            Collect();

            // An already-combined sampler declared at module scope would ALSO be emitted as a
            // GLSL uniform (SPIRV-Cross only skips SEPARATE images/samplers), landing before the
            // synthesized ones and shifting every ps_s{k}. DXC cannot produce one — SM6 HLSL has
            // no combined sampler type, and the pre-parser rewrites legacy `sampler2D` to
            // `SamplerState` + `<texture>.Sample(...)` for every non-FNA target before DXC sees
            // the source — so this is an unmodelled shape, not a supported one.
            foreach ((uint id, uint typeId) in _moduleVariables)
            {
                if (PointeeOf(typeId) is { } pointee && _sampledImageTypes.Contains(pointee))
                {
                    throw new CombinedSamplerModelException(
                        $"the module declares '{NameOf(id)}' as an already-combined sampled image. " +
                        "The declaration order of such a variable relative to the synthesized " +
                        "combined samplers is not modelled.");
                }
            }

            if (_entryFunctionId == 0 || !_functions.ContainsKey(_entryFunctionId))
                throw new CombinedSamplerModelException("the module declares no entry-point function body.");

            Walk(_entryFunctionId, new HashSet<uint>());

            var result = new List<CombinedSamplerPair>(_pairs.Count);
            foreach ((uint image, uint sampler) in _pairs)
            {
                result.Add(new CombinedSamplerPair(
                    ResolveName(image, expectImage: true),
                    ResolveName(sampler, expectImage: false)));
            }

            return Result<IReadOnlyList<CombinedSamplerPair>, ShaderError>.Ok(result);
        }

        // ---- Pass 1 ------------------------------------------------------------

        private void Collect()
        {
            FunctionBody? current = null;

            foreach (SpirvModule.Instruction instr in _module.Instructions)
            {
                uint[] ops = instr.Operands;
                switch (instr.Opcode)
                {
                    case SpirvOpcode.OpName when ops.Length >= 2:
                        _names[ops[0]] = SpirvModule.DecodeString(ops, 1);
                        break;

                    // OpEntryPoint: [executionModel, entryPointId, name...]. The FIRST one wins,
                    // matching how the SPIR-V parser sets ir.default_entry_point.
                    case SpirvOpcode.OpEntryPoint when ops.Length >= 2:
                        if (_entryFunctionId == 0)
                            _entryFunctionId = ops[1];
                        break;

                    // OpTypeImage: [resultId, sampledType, dim, depth, arrayed, MS, sampled, format].
                    // Sampled == 1 means "will be used with a sampler" — the separate-image form
                    // SPIRV-Cross combines. Sampled == 2 (storage image) is never combined.
                    case SpirvOpcode.OpTypeImage when ops.Length >= 7:
                        if (ops[6] == 1)
                            _separateImageTypes.Add(ops[0]);
                        break;

                    case SpirvOpcode.OpTypeSampler when ops.Length >= 1:
                        _samplerTypes.Add(ops[0]);
                        break;

                    case SpirvOpcode.OpTypeSampledImage when ops.Length >= 2:
                        _sampledImageTypes.Add(ops[0]);
                        break;

                    // OpTypePointer: [resultId, storageClass, typeId].
                    case SpirvOpcode.OpTypePointer when ops.Length >= 3:
                        _pointee[ops[0]] = ops[2];
                        break;

                    // OpVariable: [resultType, resultId, storageClass, (initializer)].
                    case SpirvOpcode.OpVariable when ops.Length >= 3:
                        _variableTypes[ops[1]] = ops[0];
                        if (current is null)
                            _moduleVariables.Add((ops[1], ops[0]));
                        break;

                    // OpFunction: [resultType, resultId, functionControl, functionType].
                    case SpirvOpcode.OpFunction when ops.Length >= 2:
                        current = new FunctionBody();
                        _functions[ops[1]] = current;
                        break;

                    // OpFunctionParameter: [resultType, resultId] — in declaration order.
                    case SpirvOpcode.OpFunctionParameter when ops.Length >= 2:
                        current?.Parameters.Add(ops[1]);
                        break;

                    case SpirvOpcode.OpFunctionEnd:
                        current = null;
                        break;

                    default:
                        // Every other instruction inside a function is body code, in binary
                        // order — which is what traverse_all_reachable_opcodes walks (all blocks
                        // of the function, in order, then each block's ops in order).
                        current?.Ops.Add(instr);
                        break;
                }
            }
        }

        // ---- Pass 2 ------------------------------------------------------------

        private void Walk(uint functionId, HashSet<uint> onStack)
        {
            if (!_functions.TryGetValue(functionId, out FunctionBody? body))
            {
                // A called function with no body in this module (an unresolved import). SPIR-V
                // forbids this in a complete module, so treat it as unmodelled rather than
                // silently skipping calls that might register pairs.
                throw new CombinedSamplerModelException(
                    $"function %{functionId} is called but has no body in this module.");
            }

            // SPIR-V forbids recursion, so this can only fire on malformed input; guard anyway
            // rather than recursing forever.
            if (!onStack.Add(functionId))
                throw new CombinedSamplerModelException($"function %{functionId} is recursive.");

            foreach (SpirvModule.Instruction instr in body.Ops)
            {
                uint[] ops = instr.Operands;
                switch (instr.Opcode)
                {
                    // OpLoad: [resultType, resultId, pointer]. SPIRV-Cross's handler records the
                    // backing variable only for a separate image or a separate sampler.
                    case SpirvOpcode.OpLoad when ops.Length >= 3:
                        if (IsSeparateImage(ops[0]) || IsSeparateSampler(ops[0]))
                            RegisterRead(ops[1], ops[2]);
                        break;

                    // Access chains: [resultType, resultId, base, indices...]. SPIRV-Cross throws
                    // outright on an array/struct of separate SAMPLERS ("not possible to
                    // statically remap to plain GLSL") and propagates the backing variable for a
                    // separate image.
                    case SpirvOpcode.OpAccessChain when ops.Length >= 3:
                    case SpirvOpcode.OpInBoundsAccessChain when ops.Length >= 3:
                    case SpirvOpcode.OpPtrAccessChain when ops.Length >= 3:
                        if (IsSeparateSampler(ops[0]))
                        {
                            throw new CombinedSamplerModelException(
                                "the shader indexes an array or struct of separate samplers, which " +
                                "cannot be statically remapped to plain GLSL.");
                        }
                        if (IsSeparateImage(ops[0]))
                            RegisterRead(ops[1], ops[2]);
                        break;

                    // OpSampledImage: [resultType, resultId, image, sampler]. THE trigger.
                    case SpirvOpcode.OpSampledImage when ops.Length >= 4:
                    {
                        uint image = RemapParameter(ops[2]);
                        uint sampler = RemapParameter(ops[3]);
                        if (!_pairs.Any(p => p.Image == image && p.Sampler == sampler))
                            _pairs.Add((image, sampler));
                        break;
                    }

                    // OpFunctionCall: [resultType, resultId, function, args...]. Push the
                    // callee's parameter -> caller-argument remapping (computed in the CALLER's
                    // scope, before the push), recurse, pop.
                    case SpirvOpcode.OpFunctionCall when ops.Length >= 3:
                    {
                        uint callee = ops[2];
                        if (!_functions.TryGetValue(callee, out FunctionBody? calleeBody))
                        {
                            throw new CombinedSamplerModelException(
                                $"function %{callee} is called but has no body in this module.");
                        }

                        var remap = new Dictionary<uint, uint>();
                        int argCount = Math.Min(calleeBody.Parameters.Count, ops.Length - 3);
                        for (int i = 0; i < argCount; i++)
                            remap[calleeBody.Parameters[i]] = RemapParameter(ops[3 + i]);

                        _remapping.Push(remap);
                        Walk(callee, onStack);
                        _remapping.Pop();
                        break;
                    }
                }
            }

            onStack.Remove(functionId);
        }

        /// <summary>Mirrors <c>Compiler::register_read</c>: remember the pointer's backing variable.</summary>
        private void RegisterRead(uint resultId, uint pointerId)
        {
            uint backing = BackingVariable(pointerId);
            if (backing != 0)
                _loadedFrom[resultId] = backing;
        }

        /// <summary>Mirrors <c>Compiler::maybe_get_backing_variable</c>; 0 means "not resolvable".</summary>
        private uint BackingVariable(uint id)
        {
            if (_variableTypes.ContainsKey(id))
                return id;
            return _loadedFrom.TryGetValue(id, out uint v) ? v : 0u;
        }

        /// <summary>Mirrors <c>CombinedImageSamplerHandler::remap_parameter</c>.</summary>
        private uint RemapParameter(uint id)
        {
            uint backing = BackingVariable(id);
            uint key = backing != 0 ? backing : id;
            if (_remapping.Count > 0 && _remapping.Peek().TryGetValue(key, out uint mapped))
                return mapped;
            return key;
        }

        // ---- Type helpers ------------------------------------------------------

        /// <summary>
        /// SPIRV-Cross's <c>SPIRType</c> for a pointer-to-image keeps <c>basetype == Image</c>,
        /// so its checks see through the pointer. Mirror that by testing the type and, when it is
        /// a pointer, its pointee.
        /// </summary>
        private bool IsSeparateImage(uint typeId) =>
            _separateImageTypes.Contains(typeId)
            || (PointeeOf(typeId) is { } p && _separateImageTypes.Contains(p));

        private bool IsSeparateSampler(uint typeId) =>
            _samplerTypes.Contains(typeId)
            || (PointeeOf(typeId) is { } p && _samplerTypes.Contains(p));

        private uint? PointeeOf(uint typeId) =>
            _pointee.TryGetValue(typeId, out uint p) ? p : null;

        // ---- Naming ------------------------------------------------------------

        /// <summary>Matches <c>SpirvReflectionParser.ResourceName</c> so the join by name lines up.</summary>
        private string NameOf(uint id) =>
            _names.TryGetValue(id, out string? n) && !string.IsNullOrEmpty(n) ? n : $"resource{id}";

        private string ResolveName(uint id, bool expectImage)
        {
            string kind = expectImage ? "texture" : "sampler";

            (uint Id, uint TypeId) declared = _moduleVariables.FirstOrDefault(v => v.Id == id);
            if (declared.Id != id)
            {
                throw new CombinedSamplerModelException(
                    $"a sampling operation's {kind} operand (%{id}) does not resolve to a " +
                    "module-level resource declaration, so the combined sampler it produces " +
                    "cannot be matched to an effect parameter.");
            }

            uint? pointee = PointeeOf(declared.TypeId);
            bool matches = pointee is { } p &&
                           (expectImage ? _separateImageTypes.Contains(p) : _samplerTypes.Contains(p));
            if (!matches)
            {
                throw new CombinedSamplerModelException(
                    $"'{NameOf(id)}' is used as the {kind} half of a combined sampler but is not " +
                    $"declared as a separate {kind}.");
            }

            return NameOf(id);
        }
    }
}
