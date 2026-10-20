using System;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

static class Verify
{
    static void Main(string[] args)
    {
        ModuleDefMD mod = ModuleDefMD.Load(args[0]);

        Console.WriteLine("=== LoaderInit ===");
        MethodDef m = mod.Find("PCL.PageDownloadInstall", false).FindMethod("LoaderInit");
        foreach (Instruction i in m.Body.Instructions)
            if (i.OpCode.Code != Code.Nop && i.OpCode.Code != Code.Ldarg_0) Console.WriteLine("  " + i);

        Console.WriteLine("=== ModpackInstall(3) head/tail ===");
        TypeDef t = mod.Find("PCL.ModModpack", false);
        MethodDef m3 = null, m0 = null;
        foreach (MethodDef x in t.Methods)
        {
            if (x.Name.String != "ModpackInstall") continue;
            if (x.MethodSig.Params.Count == 0) m0 = x;
            if (x.MethodSig.Params.Count == 3) m3 = x;
        }
        bool print = false;
        foreach (Instruction i in m3.Body.Instructions)
        {
            if (i.OpCode.Code == Code.Ldloc && print) { }
            if (i.OpCode.Code == Code.Ldloc) { print = true; }
            if (print && (i.OpCode.Code == Code.Leave || i.OpCode.Code == Code.Call || i.OpCode.Code == Code.Ldarg || i.OpCode.Code == Code.Ret || i.OpCode.Code == Code.Switch))
                Console.WriteLine("  " + i);
        }
        Console.WriteLine("  last instr: " + m3.Body.Instructions[m3.Body.Instructions.Count - 1]);

        Console.WriteLine("=== ModpackInstall() ===");
        foreach (Instruction i in m0.Body.Instructions) Console.WriteLine("  " + i);

        Console.WriteLine("=== Dispatch method ===");
        MethodDef d = t.FindMethod("ModpackInstallDispatch");
        if (d == null) Console.WriteLine("  MISSING");
        else
        {
            Console.WriteLine("  sig: " + d.MethodSig);
            foreach (Instruction i in d.Body.Instructions) Console.WriteLine("  " + i);
        }
    }
}
