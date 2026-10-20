using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

static class Patcher
{
    static ModuleDefMD mod;

    static void Main(string[] args)
    {
        try
        {
            Run(args);
        }
        catch (Exception e)
        {
            Console.WriteLine("FATAL: " + e);
            Environment.Exit(1);
        }
    }

    static void Run(string[] args)
    {
        string src = args[0];
        string dst = args[1];
        mod = ModuleDefMD.Load(src);

        // 1) ModSecret.RestrictMcDownload = False
        TypeDef modSecret = mod.Find("PCL.ModSecret", false);
        FieldDef restrict = modSecret.FindField("RestrictMcDownload");
        restrict.Constant.Value = false;
        Console.WriteLine("[1] RestrictMcDownload -> " + restrict.Constant.Value);

        PatchLoaderInit();
        PatchVisibility();
        PatchModpackInstall();
        PatchModpackInstallNoArg();

        mod.Write(dst);
        Console.WriteLine("[OK] written: " + dst);
    }

    // 2) PageDownloadInstall.LoaderInit: replace the "restricted" throwing loader with the real DlClientListLoader
    static void PatchLoaderInit()
    {
        TypeDef t = mod.Find("PCL.PageDownloadInstall", false);
        MethodDef m = t.FindMethod("LoaderInit");
        IList<Instruction> ins = m.Body.Instructions;

        int k = -1;
        for (int i = 0; i + 12 < ins.Count; i++)
        {
            if (ins[i].OpCode.Code != Code.Ldstr) continue;
            if (ins[i + 12].OpCode.Code != Code.Newobj) continue;
            IMethod ctor = ins[i + 12].Operand as IMethod;
            if (ctor == null || ctor.DeclaringType == null) continue;
            if (!ctor.DeclaringType.Name.String.Contains("LoaderTask")) continue;
            k = i;
            break;
        }
        if (k < 0) throw new Exception("LoaderInit pattern not found");

        FieldDef dl = mod.Find("PCL.ModDownload", false).FindField("DlClientListLoader");
        ins[k].OpCode = OpCodes.Ldsfld;
        ins[k].Operand = dl;
        for (int j = 1; j <= 12; j++)
        {
            ins[k + j].OpCode = OpCodes.Nop;
            ins[k + j].Operand = null;
        }
        Console.WriteLine("[2] LoaderInit -> DlClientListLoader");
    }

    // 3) PageDownloadModify.LoadInstanceFinished: CardMinecraft.Visibility = Visible
    static void PatchVisibility()
    {
        TypeDef t = mod.Find("PCL.PageDownloadModify", false);
        int n = 0;
        foreach (MethodDef m in t.Methods)
        {
            if (!m.HasBody) continue;
            IList<Instruction> ins = m.Body.Instructions;
            for (int i = 0; i + 2 < ins.Count; i++)
            {
                IMethod get = ins[i].Operand as IMethod;
                if (get == null || get.Name.String != "get_CardMinecraft") continue;
                if (ins[i + 1].OpCode.Code != Code.Ldc_I4_2) continue;
                IMethod set = ins[i + 2].Operand as IMethod;
                if (set == null || set.Name.String != "set_Visibility") continue;
                ins[i + 1].OpCode = OpCodes.Ldc_I4_0;
                ins[i + 1].Operand = null;
                n++;
            }
        }
        if (n == 0) throw new Exception("CardMinecraft visibility pattern not found");
        Console.WriteLine("[3] CardMinecraft visible sites: " + n);
    }

    // 4) ModpackInstall(File, InstanceName, Logo): restore the pack-type dispatch that was removed by const folding
    static void PatchModpackInstall()
    {
        TypeDef t = mod.Find("PCL.ModModpack", false);
        MethodDef target = null;
        foreach (MethodDef m in t.Methods)
        {
            if (m.Name.String == "ModpackInstall" && m.HasBody && m.Body.Instructions.Count > 100)
            {
                target = m;
                break;
            }
        }
        if (target == null) throw new Exception("ModpackInstall(3 args) not found");

        CilBody body = target.Body;
        IList<Instruction> ins = body.Instructions;

        int ti = -1;
        for (int i = 0; i < ins.Count; i++)
        {
            if (ins[i].OpCode.Code != Code.Throw) continue;
            if (i < 2) continue;
            if (ins[i - 1].OpCode.Code != Code.Newobj) continue;
            if (ins[i - 2].OpCode.Code != Code.Ldstr) continue;
            string s = ins[i - 2].Operand as string;
            if (s == null || !s.Contains("qwq")) continue;
            ti = i;
            break;
        }
        if (ti < 0) throw new Exception("restriction throw not found");

        Instruction ldstrIns = ins[ti - 2];
        Instruction newobjIns = ins[ti - 1];
        Instruction throwIns = ins[ti];

        Local v0 = body.Variables[0];
        Local v1 = body.Variables[1];
        Local v2 = body.Variables[2];
        Local v4 = body.Variables[4];

        MethodDef dispatch = CreateDispatch(t, target);
        Instruction lout = Instruction.Create(OpCodes.Ldloc, v0);

        ldstrIns.OpCode = OpCodes.Ldloc;
        ldstrIns.Operand = v4;

        List<Instruction> seq = new List<Instruction>();
        seq.Add(Instruction.Create(OpCodes.Ldarg_0));
        seq.Add(Instruction.Create(OpCodes.Ldloc, v1));
        seq.Add(Instruction.Create(OpCodes.Ldloc, v2));
        seq.Add(Instruction.Create(OpCodes.Ldarg_1));
        seq.Add(Instruction.Create(OpCodes.Ldarg_2));
        seq.Add(Instruction.Create(OpCodes.Call, dispatch));
        seq.Add(Instruction.Create(OpCodes.Stloc, v0));
        seq.Add(Instruction.Create(OpCodes.Leave, lout));

        int start = ins.IndexOf(ldstrIns);
        ins.RemoveAt(start + 2);
        ins.RemoveAt(start + 1);
        for (int j = 0; j < seq.Count; j++) ins.Insert(start + 1 + j, seq[j]);

        // In dnlib, HandlerEnd == null means "the finally extends to the end of the method".
        // We are appending code at the end, so the leave target would land INSIDE the
        // protected region -> invalid IL -> InvalidProgramException at JIT time.
        // Insert a real boundary NOP after endfinally and point the finally's HandlerEnd
        // at it, so the appended return path sits outside the protected region.
        Instruction endMarker = Instruction.Create(OpCodes.Nop);
        ins.Add(endMarker);
        ins.Add(lout);
        ins.Add(Instruction.Create(OpCodes.Ret));
        foreach (ExceptionHandler eh in body.ExceptionHandlers)
        {
            if (eh.HandlerType == ExceptionHandlerType.Finally && eh.HandlerEnd == null)
                eh.HandlerEnd = endMarker;
        }
        Console.WriteLine("[4] ModpackInstall dispatch restored (finally HandlerEnd fixed)");
    }

    static MethodDef CreateDispatch(TypeDef t, MethodDef orig)
    {
        TypeSig zip = orig.Body.Variables[1].Type;
        MethodSig sig = MethodSig.CreateStatic(orig.ReturnType, new TypeSig[] {
            mod.CorLibTypes.Int32, mod.CorLibTypes.String, zip,
            mod.CorLibTypes.String, mod.CorLibTypes.String, mod.CorLibTypes.String
        });
        MethodDef md = new MethodDefUser("ModpackInstallDispatch", sig, MethodImplAttributes.IL | MethodImplAttributes.Managed,
            MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);
        t.Methods.Add(md);
        CilBody b = new CilBody();
        b.InitLocals = false;
        md.Body = b;

        if (md.ParamDefs.Count != 6)
        {
            md.ParamDefs.Clear();
            for (int i = 0; i < 6; i++) md.ParamDefs.Add(new ParamDefUser("arg" + i, (ushort)(i + 1)));
        }

        MethodDef mCurse = FindMethod(t, "InstallPackCurseForge");
        MethodDef mModrinth = FindMethod(t, "InstallPackModrinth");
        MethodDef mHmcl = FindMethod(t, "InstallPackHMCL");
        MethodDef mMmc = FindMethod(t, "InstallPackMMC");
        MethodDef mMcbbs = FindMethod(t, "InstallPackMCBBS");
        MethodDef mLauncher = FindMethod(t, "InstallPackLauncherPack");
        MethodDef mCompress = FindMethod(t, "InstallPackCompress");

        Instruction c0 = Instruction.Create(OpCodes.Nop);
        Instruction c1 = Instruction.Create(OpCodes.Nop);
        Instruction c2 = Instruction.Create(OpCodes.Nop);
        Instruction c3 = Instruction.Create(OpCodes.Nop);
        Instruction c4 = Instruction.Create(OpCodes.Nop);
        Instruction c9 = Instruction.Create(OpCodes.Nop);
        Instruction cElse = Instruction.Create(OpCodes.Nop);

        b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[0]));
        b.Instructions.Add(Instruction.Create(OpCodes.Switch, new List<Instruction> { c0, c1, c2, c3, c4 }));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[0]));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, 9));
        b.Instructions.Add(Instruction.Create(OpCodes.Beq, c9));
        b.Instructions.Add(Instruction.Create(OpCodes.Br, cElse));

        AddCase(b, md, c0, mCurse, new int[] { 1, 2, 3, 4, 5 });
        AddCase(b, md, c1, mHmcl, new int[] { 1, 2, 3 });
        AddCase(b, md, c2, mMmc, new int[] { 1, 2, 3 });
        AddCase(b, md, c3, mMcbbs, new int[] { 1, 2, 3, 4 });
        AddCase(b, md, c4, mModrinth, new int[] { 1, 2, 3, 4, 5 });
        AddCase(b, md, c9, mLauncher, new int[] { 1, 2, 3 });
        AddCase(b, md, cElse, mCompress, new int[] { 1, 2 });
        return md;
    }

    static void AddCase(CilBody b, MethodDef md, Instruction label, MethodDef target, int[] args)
    {
        b.Instructions.Add(label);
        for (int i = 0; i < args.Length; i++)
            b.Instructions.Add(Instruction.Create(OpCodes.Ldarg, md.Parameters[args[i]]));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, target));
        b.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }

    // 5) ModpackInstall(): restore file picker + install call
    static void PatchModpackInstallNoArg()
    {
        TypeDef t = mod.Find("PCL.ModModpack", false);
        MethodDef m0 = null;
        MethodDef m3 = null;
        foreach (MethodDef m in t.Methods)
        {
            if (m.Name.String != "ModpackInstall") continue;
            if (m.MethodSig.Params.Count == 0) m0 = m;
            else if (m.MethodSig.Params.Count == 3) m3 = m;
        }
        if (m0 == null || m3 == null) throw new Exception("ModpackInstall overloads not found");

        MethodDef jm = mod.Find("PCL.ModJava", false).FindMethod("ManuallyImportJava");
        IList<Instruction> jins = jm.Body.Instructions;
        IMethod selectFile = null, oneOf = null, nullableCtor = null, firstOrDefault = null;
        for (int i = 3; i + 2 < jins.Count; i++)
        {
            IMethod im = jins[i].Operand as IMethod;
            if (im == null || im.Name.String != "SelectFile") continue;
            selectFile = im;
            oneOf = jins[i - 2].Operand as IMethod;
            nullableCtor = jins[i - 1].Operand as IMethod;
            firstOrDefault = jins[i + 1].Operand as IMethod;
            break;
        }
        if (selectFile == null || oneOf == null || nullableCtor == null || firstOrDefault == null)
            throw new Exception("SelectFile refs not found");

        IMethod isNullOrEmpty = FindCall("IsNullOrEmpty");
        if (isNullOrEmpty == null) throw new Exception("IsNullOrEmpty ref not found");

        CilBody b = m0.Body;
        b.Instructions.Clear();
        b.ExceptionHandlers.Clear();
        b.Variables.Clear();
        Local vFile = new Local(mod.CorLibTypes.String);
        b.Variables.Add(vFile);
        b.InitLocals = true;

        Instruction ret = Instruction.Create(OpCodes.Ret);
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "Select modpack file"));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, "rar;zip;mrpack|Modpack files"));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, oneOf));
        b.Instructions.Add(Instruction.Create(OpCodes.Newobj, nullableCtor));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, selectFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, firstOrDefault));
        b.Instructions.Add(Instruction.Create(OpCodes.Stloc, vFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, isNullOrEmpty));
        b.Instructions.Add(Instruction.Create(OpCodes.Brtrue, ret));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldloc, vFile));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
        b.Instructions.Add(Instruction.Create(OpCodes.Call, m3));
        b.Instructions.Add(Instruction.Create(OpCodes.Pop));
        b.Instructions.Add(ret);
        Console.WriteLine("[5] ModpackInstall() file picker restored");
    }

    static MethodDef FindMethod(TypeDef t, string name)
    {
        foreach (MethodDef m in t.Methods)
            if (m.Name.String == name) return m;
        throw new Exception("method not found: " + name);
    }

    static IMethod FindCall(string name)
    {
        foreach (TypeDef t in mod.GetTypes())
        {
            foreach (MethodDef m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (Instruction ins in m.Body.Instructions)
                {
                    IMethod im = ins.Operand as IMethod;
                    if (im != null && im.Name.String == name) return im;
                }
            }
        }
        return null;
    }
}
