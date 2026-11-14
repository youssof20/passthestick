using System.Reflection;
var asm = typeof(DataChannelDotnet.IRtcDataChannel).Assembly;
foreach (var name in new[] { "RtcDescription", "RtcCandidate", "RtcDescriptionType", "RtcCandidatePair" })
{
    var t = asm.GetTypes().FirstOrDefault(x => x.Name == name);
    Console.WriteLine("== " + name + (t == null ? " (missing)" : ""));
    if (t == null) continue;
    if (t.IsEnum) { Console.WriteLine("  " + string.Join(",", Enum.GetNames(t))); continue; }
    foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.MemberType is MemberTypes.Property or MemberTypes.Constructor or MemberTypes.Field))
        Console.WriteLine("  " + m);
}
Console.WriteLine(string.Join(",", Enum.GetNames(asm.GetTypes().First(x => x.Name == "rtcState"))));
