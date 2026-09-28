// Adds the input hooks the online add-on needs to sparrow's "BALLxPIT: Local Coop" 0.1.0.
//
//   Patcher <original BALLxPITLocalCoop.dll> <patched output dir> <reference output dir>
//
// The patched copy is what players install. It behaves exactly like the original until the online
// add-on registers its callbacks:
//   * Every Input.GetKey(KeyCode) read inside PlayerTwoController goes through OnlineHooks.GetKey,
//     which also reports keys a remote guest is holding (P2's Shoot key, for example).
//   * UpdatePlayerTwoInput calls OnlineHooks.OnBeforeApplyInput(this) right before it applies P2's
//     movement and aim, so the add-on can put the guest's stick and aim into P2's fields.
// The reference copy is the same assembly with every member made public, used only to compile the
// add-on (which reaches the real private members at runtime through IgnoresAccessChecksTo).
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: Patcher <original BALLxPITLocalCoop.dll> <patched out dir> <reference out dir>");
    return 2;
}

const string HooksNamespace = "BALLxPITLocalCoop";
const string HooksName = "OnlineHooks";
const int HooksVersion = 1;

try
{
    string patchedPath = Path.Combine(args[1], "BALLxPITLocalCoop.dll");
    string referencePath = Path.Combine(args[2], "BALLxPITLocalCoop.dll");
    Directory.CreateDirectory(args[1]);
    Directory.CreateDirectory(args[2]);

    using (var module = ModuleDefinition.ReadModule(args[0], new ReaderParameters { ReadingMode = ReadingMode.Immediate, InMemory = true }))
    {
        Patch(module);
        module.Write(patchedPath);
    }
    using (var module = ModuleDefinition.ReadModule(patchedPath, new ReaderParameters { InMemory = true }))
    {
        Publicize(module);
        module.Write(referencePath);
    }
    Console.WriteLine($"Patched: {patchedPath}");
    Console.WriteLine($"Reference: {referencePath}");
    return 0;
}
catch (PatchException ex)
{
    Console.Error.WriteLine("Patch failed: " + ex.Message);
    return 1;
}

static void Patch(ModuleDefinition module)
{
    if (module.Assembly.Name.Name != "BALLxPITLocalCoop")
        throw new PatchException($"expected the BALLxPITLocalCoop assembly, got {module.Assembly.Name.Name}.");
    if (module.GetType(HooksNamespace, HooksName) != null)
        throw new PatchException("this file is already patched. Give the patcher the original download.");

    TypeDefinition controller = module.GetType("BALLxPITLocalCoop.PlayerTwoController")
        ?? throw new PatchException("PlayerTwoController not found; this is not Local Coop 0.1.0.");
    MethodDefinition updateInput = controller.Methods.SingleOrDefault(m => m.Name == "UpdatePlayerTwoInput" && m.Parameters.Count == 0)
        ?? throw new PatchException("PlayerTwoController.UpdatePlayerTwoInput() not found.");

    MethodReference inputGetKey = controller.Methods
        .Where(m => m.HasBody)
        .SelectMany(m => m.Body.Instructions)
        .Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && IsInputGetKey(r))
        .Select(i => (MethodReference)i.Operand)
        .FirstOrDefault() ?? throw new PatchException("PlayerTwoController never calls Input.GetKey(KeyCode).");
    TypeReference keyCode = inputGetKey.Parameters[0].ParameterType;

    TypeDefinition hooks = BuildHooksType(module, controller, inputGetKey, keyCode);
    MethodDefinition hookGetKey = hooks.Methods.Single(m => m.Name == "GetKey");
    MethodDefinition hookBeforeApply = hooks.Methods.Single(m => m.Name == "OnBeforeApplyInput");

    // 1. Route P2's key reads through the hook.
    int redirected = 0;
    var methodsWithRedirect = new HashSet<string>();
    foreach (MethodDefinition method in controller.Methods.Where(m => m.HasBody))
    {
        foreach (Instruction instruction in method.Body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference r && IsInputGetKey(r))
            {
                instruction.Operand = hookGetKey;
                redirected++;
                methodsWithRedirect.Add(method.Name);
            }
        }
    }
    foreach (string required in new[] { "UpdatePlayerTwoShootControls", "TryPlayerTwoShootLateFrame" })
    {
        if (!methodsWithRedirect.Contains(required))
            throw new PatchException($"{required} no longer reads the Shoot key through Input.GetKey.");
    }

    // 2. Call the add-on right before UpdatePlayerTwoInput applies movement:
    //        ldarg.0 ; ldc.i4.0 ; call ApplyPlayerTwoMovementOverrides(bool)
    //    becomes
    //        ldarg.0 ; call OnlineHooks.OnBeforeApplyInput(PlayerTwoController) ; ldarg.0 ; ldc.i4.0 ; call ...
    MethodBody body = updateInput.Body;
    if (body.HasExceptionHandlers)
        throw new PatchException("UpdatePlayerTwoInput has exception handlers; its layout changed.");
    Instruction[] calls = body.Instructions
        .Where(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "ApplyPlayerTwoMovementOverrides"
            && r.DeclaringType.FullName == controller.FullName)
        .ToArray();
    if (calls.Length != 1)
        throw new PatchException($"expected one ApplyPlayerTwoMovementOverrides call in UpdatePlayerTwoInput, found {calls.Length}.");
    Instruction boolArg = calls[0].Previous;
    Instruction thisArg = boolArg?.Previous!;
    if (boolArg == null || boolArg.OpCode != OpCodes.Ldc_I4_0 || thisArg == null || thisArg.OpCode != OpCodes.Ldarg_0)
        throw new PatchException("the ApplyPlayerTwoMovementOverrides(false) call in UpdatePlayerTwoInput changed shape.");

    body.SimplifyMacros();
    ILProcessor il = body.GetILProcessor();
    Instruction hookThis = il.Create(OpCodes.Ldarg_0);
    Instruction hookCall = il.Create(OpCodes.Call, hookBeforeApply);
    il.InsertBefore(thisArg, hookThis);
    il.InsertAfter(hookThis, hookCall);
    int retargeted = 0;
    foreach (Instruction instruction in body.Instructions)
    {
        if (instruction.Operand == thisArg)
        {
            instruction.Operand = hookThis;
            retargeted++;
        }
        else if (instruction.Operand is Instruction[] targets)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == thisArg)
                {
                    targets[i] = hookThis;
                    retargeted++;
                }
            }
        }
    }
    body.OptimizeMacros();

    // 3. Mark the plugin so logs and BepInEx's duplicate check can tell the builds apart. A higher
    //    version makes BepInEx pick this copy if the original DLL is still installed.
    TypeDefinition plugin = module.GetType("BALLxPITLocalCoop.Plugin") ?? throw new PatchException("Plugin class not found.");
    CustomAttribute bepInPlugin = plugin.CustomAttributes.SingleOrDefault(a => a.AttributeType.FullName == "BepInEx.BepInPlugin")
        ?? throw new PatchException("[BepInPlugin] not found.");
    string oldVersion = (string)bepInPlugin.ConstructorArguments[2].Value;
    if (oldVersion != "0.1.0")
        throw new PatchException($"this patcher is written for Local Coop 0.1.0, the file is {oldVersion}.");
    bepInPlugin.ConstructorArguments[1] = new CustomAttributeArgument(module.TypeSystem.String, "BALLxPIT: Local Coop (online-ready)");
    bepInPlugin.ConstructorArguments[2] = new CustomAttributeArgument(module.TypeSystem.String, "0.1.1-online");

    Console.WriteLine($"Redirected {redirected} Input.GetKey reads in PlayerTwoController ({string.Join(", ", methodsWithRedirect.OrderBy(n => n))}).");
    Console.WriteLine($"Inserted OnBeforeApplyInput in UpdatePlayerTwoInput; retargeted {retargeted} branch(es).");
}

static bool IsInputGetKey(MethodReference r) =>
    r.Name == "GetKey" && r.DeclaringType.FullName == "UnityEngine.Input" && r.Parameters.Count == 1
    && r.Parameters[0].ParameterType.FullName == "UnityEngine.KeyCode" && r.ReturnType.MetadataType == MetadataType.Boolean;

static TypeDefinition BuildHooksType(ModuleDefinition module, TypeDefinition controller, MethodReference inputGetKey, TypeReference keyCode)
{
    AssemblyNameReference systemRuntime = module.AssemblyReferences.SingleOrDefault(a => a.Name == "System.Runtime")
        ?? throw new PatchException("the assembly does not reference System.Runtime.");

    var hooks = new TypeDefinition(HooksNamespace, HooksName,
        TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class,
        module.TypeSystem.Object);
    module.Types.Add(hooks);

    // Func<KeyCode, bool>
    var funcOpen = new TypeReference("System", "Func`2", module, systemRuntime);
    funcOpen.GenericParameters.Add(new GenericParameter("T", funcOpen));
    funcOpen.GenericParameters.Add(new GenericParameter("TResult", funcOpen));
    var funcKeyBool = new GenericInstanceType(funcOpen);
    funcKeyBool.GenericArguments.Add(keyCode);
    funcKeyBool.GenericArguments.Add(module.TypeSystem.Boolean);
    var funcInvoke = new MethodReference("Invoke", funcOpen.GenericParameters[1], funcKeyBool) { HasThis = true };
    funcInvoke.Parameters.Add(new ParameterDefinition(funcOpen.GenericParameters[0]));

    // Action<PlayerTwoController>
    var actionOpen = new TypeReference("System", "Action`1", module, systemRuntime);
    actionOpen.GenericParameters.Add(new GenericParameter("T", actionOpen));
    var actionController = new GenericInstanceType(actionOpen);
    actionController.GenericArguments.Add(controller);
    var actionInvoke = new MethodReference("Invoke", module.TypeSystem.Void, actionController) { HasThis = true };
    actionInvoke.Parameters.Add(new ParameterDefinition(actionOpen.GenericParameters[0]));

    var remoteKeyHeld = new FieldDefinition("RemoteKeyHeld", FieldAttributes.Public | FieldAttributes.Static, funcKeyBool);
    var beforeApplyInput = new FieldDefinition("BeforeApplyInput", FieldAttributes.Public | FieldAttributes.Static, actionController);
    hooks.Fields.Add(remoteKeyHeld);
    hooks.Fields.Add(beforeApplyInput);

    // public static int GetVersion() => HooksVersion;
    var getVersion = new MethodDefinition("GetVersion", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Int32);
    {
        ILProcessor il = getVersion.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4, HooksVersion);
        il.Emit(OpCodes.Ret);
    }
    hooks.Methods.Add(getVersion);

    // public static bool GetKey(KeyCode key)
    // {
    //     if (Input.GetKey(key)) return true;
    //     Func<KeyCode, bool> remote = RemoteKeyHeld;
    //     return remote != null && remote(key);
    // }
    var getKey = new MethodDefinition("GetKey", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Boolean);
    getKey.Parameters.Add(new ParameterDefinition("key", ParameterAttributes.None, keyCode));
    {
        ILProcessor il = getKey.Body.GetILProcessor();
        Instruction localHeld = il.Create(OpCodes.Ldc_I4_1);
        Instruction noRemote = il.Create(OpCodes.Pop);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, inputGetKey);
        il.Emit(OpCodes.Brtrue, localHeld);
        il.Emit(OpCodes.Ldsfld, remoteKeyHeld);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, noRemote);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, funcInvoke);
        il.Emit(OpCodes.Ret);
        il.Append(noRemote);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        il.Append(localHeld);
        il.Emit(OpCodes.Ret);
    }
    hooks.Methods.Add(getKey);

    // public static void OnBeforeApplyInput(PlayerTwoController controller)
    // {
    //     Action<PlayerTwoController> callback = BeforeApplyInput;
    //     if (callback != null) callback(controller);
    // }
    var onBeforeApply = new MethodDefinition("OnBeforeApplyInput", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
    onBeforeApply.Parameters.Add(new ParameterDefinition("controller", ParameterAttributes.None, controller));
    {
        ILProcessor il = onBeforeApply.Body.GetILProcessor();
        Instruction noCallback = il.Create(OpCodes.Pop);
        il.Emit(OpCodes.Ldsfld, beforeApplyInput);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, noCallback);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, actionInvoke);
        il.Emit(OpCodes.Ret);
        il.Append(noCallback);
        il.Emit(OpCodes.Ret);
    }
    hooks.Methods.Add(onBeforeApply);

    foreach (MethodDefinition method in hooks.Methods)
        method.Body.OptimizeMacros();
    return hooks;
}

static void Publicize(ModuleDefinition module)
{
    foreach (TypeDefinition type in module.GetTypes())
    {
        if (type.IsNested) type.IsNestedPublic = true;
        else type.IsPublic = true;
        foreach (FieldDefinition field in type.Fields) field.IsPublic = true;
        foreach (MethodDefinition method in type.Methods) method.IsPublic = true;
    }
}

sealed class PatchException : Exception
{
    public PatchException(string message) : base(message) { }
}
