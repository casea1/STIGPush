using System.Linq;
using System.Text;

namespace StigGpoManager.Core
{
    /// <summary>
    /// Builds Get-GPOReport style XML. Used by the unit tests and by /demo mode to create realistic
    /// GPO contents without a domain.
    /// </summary>
    public static class GpoReportXml
    {

        public static string UserRight(string right, params string[] members) =>
            $"<q1:UserRightsAssignment><q1:Name>{right}</q1:Name>" +
            string.Concat(members.Select(m => $"<q1:Member><Name xmlns=\"http://www.microsoft.com/GroupPolicy/Types\">{m}</Name></q1:Member>")) +
            "</q1:UserRightsAssignment>";

        /// <summary>User right with members as GPMC writes them: SID and name (name may be empty for an unresolved SID).</summary>
        public static string UserRightSids(string right, params (string name, string sid)[] members) =>
            $"<q1:UserRightsAssignment><q1:Name>{right}</q1:Name>" +
            string.Concat(members.Select(m => "<q1:Member>" + (m.sid == null ? "" : $"<SID xmlns=\"http://www.microsoft.com/GroupPolicy/Types\">{m.sid}</SID>") +
                                              (m.name == null ? "" : $"<Name xmlns=\"http://www.microsoft.com/GroupPolicy/Types\">{m.name}</Name>") + "</q1:Member>")) +
            "</q1:UserRightsAssignment>";

        /// <summary>A registry-based policy setting (e.g. App Privacy LetAppsActivateWithVoice) as an Administrative Templates item.</summary>
        public static string RegistryValue(string keyPath, string valueName, int value) =>
            $"<q2:RegistrySetting><q2:KeyPath>{keyPath}</q2:KeyPath><q2:Value><q2:Name>{valueName}</q2:Name><q2:Number>{value}</q2:Number></q2:Value></q2:RegistrySetting>";

        public static string Account(string name, int value) =>
            $"<q1:Account><q1:Name>{name}</q1:Name><q1:SettingNumber>{value}</q1:SettingNumber><q1:Type>Password</q1:Type></q1:Account>";

        public static string SecOption(string key, int value, string display) =>
            $"<q1:SecurityOptions><q1:KeyName>{key}</q1:KeyName><q1:SettingNumber>{value}</q1:SettingNumber><q1:Display><q1:Name>{display}</q1:Name><q1:DisplayBoolean>{(value == 1 ? "true" : "false")}</q1:DisplayBoolean></q1:Display></q1:SecurityOptions>";

        public static string Policy(string category, string name, string state) =>
            $"<q2:Policy><q2:Name>{name}</q2:Name><q2:State>{state}</q2:State><q2:Explain>Long help text</q2:Explain><q2:Supported>At least Windows 10</q2:Supported><q2:Category>{category}</q2:Category></q2:Policy>";

        public static string Audit(string sub, string guid, int value) =>
            $"<q3:AuditSetting><q3:PolicyTarget>System</q3:PolicyTarget><q3:SubcategoryName>{sub}</q3:SubcategoryName><q3:SubcategoryGuid>{guid}</q3:SubcategoryGuid><q3:SettingValue>{value}</q3:SettingValue></q3:AuditSetting>";

        /// <summary>A Get-GPOReport style document. Items are routed to the right extension by prefix.</summary>
        public static string Report(string name, params string[] items)
        {
            var sec = string.Concat(items.Where(i => i.StartsWith("<q1:")));
            var reg = string.Concat(items.Where(i => i.StartsWith("<q2:")));
            var aud = string.Concat(items.Where(i => i.StartsWith("<q3:")));
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<GPO xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns=\"http://www.microsoft.com/GroupPolicy/Settings\">");
            sb.Append($"<Identifier><Identifier xmlns=\"http://www.microsoft.com/GroupPolicy/Types\">{{00000000-0000-0000-0000-000000000000}}</Identifier></Identifier><Name>{name}</Name>");
            sb.Append("<Computer><VersionDirectory>1</VersionDirectory><VersionSysvol>1</VersionSysvol><Enabled>true</Enabled>");
            if (sec.Length > 0) sb.Append($"<ExtensionData><Extension xmlns:q1=\"http://www.microsoft.com/GroupPolicy/Settings/Security\" xsi:type=\"q1:SecuritySettings\">{sec}</Extension><Name>Security</Name></ExtensionData>");
            if (reg.Length > 0) sb.Append($"<ExtensionData><Extension xmlns:q2=\"http://www.microsoft.com/GroupPolicy/Settings/Registry\" xsi:type=\"q2:RegistrySettings\">{reg}</Extension><Name>Registry</Name></ExtensionData>");
            if (aud.Length > 0) sb.Append($"<ExtensionData><Extension xmlns:q3=\"http://www.microsoft.com/GroupPolicy/Settings/Auditing\" xsi:type=\"q3:AuditSettings\">{aud}</Extension><Name>Advanced Audit Configuration</Name></ExtensionData>");
            sb.Append("</Computer><User><VersionDirectory>0</VersionDirectory><VersionSysvol>0</VersionSysvol><Enabled>true</Enabled></User>");
            sb.Append("<LinksTo/></GPO>");
            return sb.ToString();
        }

    }
}
