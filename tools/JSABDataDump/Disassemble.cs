using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;

namespace JSABDataDump;

/// <summary>
/// Logs the native x64 code of a few small game methods, to read constants that only exist in compiled code
/// (for example how many triangles the tutorial has).
/// </summary>
internal static class Disassemble
{
    public static void Log()
    {
        Method(typeof(LogicTriangleToCollectTutorial), "reachLastScene");
        Method(typeof(LogicTriangleToCollectTutorial), "isStuckedScene");
        Method(typeof(LogicTriangleToCollectTutorial), "isStuckedSceneOfAfter");
        Method(typeof(ActorTutorialLevelLogic), "canDash");
        Method(typeof(ActorTutorialLevelLogic), "getSceneIndex");
    }

    private static void Method(Type type, string name, int maxInstructions = 60)
    {
        try
        {
            FieldInfo? field = null;
            foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                if (f.Name.StartsWith($"NativeMethodInfoPtr_{name}_")) { field = f; break; }
            if (field == null)
            {
                DumpPlugin.Instance.Log.LogWarning($"[asm] {type.Name}.{name}: no method info field");
                return;
            }
            var methodInfo = (IntPtr)field.GetValue(null)!;
            var code = Marshal.ReadIntPtr(methodInfo); // Il2CppMethodInfo.methodPointer is the first field
            var bytes = new byte[maxInstructions * 15];
            Marshal.Copy(code, bytes, 0, bytes.Length);

            var decoder = Iced.Intel.Decoder.Create(64, new ByteArrayCodeReader(bytes));
            decoder.IP = (ulong)code.ToInt64();
            var formatter = new NasmFormatter();
            var output = new StringOutput();
            var text = new StringBuilder($"[asm] {type.Name}.{name} @ {code.ToInt64():X}\n");
            for (int i = 0; i < maxInstructions; i++)
            {
                decoder.Decode(out var instruction);
                formatter.Format(instruction, output);
                text.AppendLine($"  {instruction.IP:X}  {output.ToStringAndReset()}");
                if (instruction.Mnemonic == Mnemonic.Ret || instruction.Mnemonic == Mnemonic.Int3) break;
            }
            DumpPlugin.Instance.Log.LogInfo(text.ToString());
        }
        catch (Exception e)
        {
            DumpPlugin.Instance.Log.LogError($"[asm] {type.Name}.{name}: {e}");
        }
    }
}
