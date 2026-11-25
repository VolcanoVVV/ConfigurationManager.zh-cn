using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
var output = new List<object>();
foreach (var path in args) {
 using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
 var md = pe.GetMetadataReader(); var asm = md.GetAssemblyDefinition();
 string Token(byte[] key) => key.Length == 0 ? "null" : Convert.ToHexString(SHA1.HashData(key).TakeLast(8).Reverse().ToArray()).ToLowerInvariant();
 var references = md.AssemblyReferences.Select(handle => {
  var r = md.GetAssemblyReference(handle);
  return new { Name = md.GetString(r.Name), Version = r.Version.ToString(), Token = r.Flags.HasFlag(AssemblyFlags.PublicKey) ? Token(md.GetBlobBytes(r.PublicKeyOrToken)) : Convert.ToHexString(md.GetBlobBytes(r.PublicKeyOrToken)).ToLowerInvariant() };
 }).ToArray();
 string framework = "";
 foreach (var handle in asm.GetCustomAttributes()) {
  var a = md.GetCustomAttribute(handle);
  if (a.Constructor.Kind != HandleKind.MemberReference) continue;
  var member = md.GetMemberReference((MemberReferenceHandle)a.Constructor);
  if (member.Parent.Kind != HandleKind.TypeReference) continue;
  var type = md.GetTypeReference((TypeReferenceHandle)member.Parent);
  if (md.GetString(type.Name) == "TargetFrameworkAttribute") {
   var value = md.GetBlobReader(a.Value); value.ReadUInt16(); framework = value.ReadSerializedString() ?? "";
  }
 }
 var api = new HashSet<string>();
 foreach (var handle in md.TypeDefinitions) {
  var type = md.GetTypeDefinition(handle);
  var name = md.GetString(type.Namespace) + "." + md.GetString(type.Name);
  if (name is "Newtonsoft.Json.JsonConvert" or "Newtonsoft.Json.Linq.JObject" or "Newtonsoft.Json.JsonLoadSettings" or "Newtonsoft.Json.Linq.JsonLoadSettings") {
   api.Add(name);
   foreach(var method in type.GetMethods()) api.Add(name + "." + md.GetString(md.GetMethodDefinition(method).Name));
  }
 }
 output.Add(new { Path = Path.GetFullPath(path), Mvid = md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N"), Name = md.GetString(asm.Name), Version = asm.Version.ToString(), PublicKeyToken = Token(md.GetBlobBytes(asm.PublicKey)), TargetFramework = framework,
  IsInterop = references.Any(r => r.Name.StartsWith("Il2CppInterop") || r.Name == "Il2Cppmscorlib"), References = references,
  HasJsonApi = api.Contains("Newtonsoft.Json.JsonConvert.SerializeObject") && api.Contains("Newtonsoft.Json.Linq.JObject.Parse") && api.Contains("Newtonsoft.Json.Linq.JsonLoadSettings") });
}
Console.WriteLine(JsonSerializer.Serialize(output));
