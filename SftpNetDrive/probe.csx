using Renci.SshNet;
using Renci.SshNet.Sftp;
using DokanNet;
using DokanNet.Logging;
var t1 = typeof(SftpClient).GetMethod("GetStatus");
Console.WriteLine("SftpClient.GetStatus: " + (t1?.ToString() ?? "NOT FOUND"));
var t2 = typeof(Dokan).GetConstructors();
foreach(var c in t2) { Console.WriteLine("Dokan ctor: " + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))); }
var t3 = typeof(Dokan).GetMethods(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Static);
foreach(var m in t3.Where(m=>m.DeclaringType==typeof(Dokan))) { Console.WriteLine((m.IsStatic?"static ":"") + m.Name + "(" + string.Join(", ",m.GetParameters().Select(p=>p.ParameterType.Name+" "+p.Name)) + ")"); }
