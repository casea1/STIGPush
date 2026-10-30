using System;
using System.Collections.Generic;

namespace StigGpoManager.Core
{
    public sealed class GpoInfo
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime ModificationTime { get; set; }
        public string GpoStatus { get; set; }
    }

    public sealed class DomainInfo
    {
        public string DnsRoot { get; set; }
        public string NetBiosName { get; set; }
        public string DistinguishedName { get; set; }
        public string PdcEmulator { get; set; }
        public List<string> DomainControllers { get; set; } = new List<string>();
        public string DomainSid { get; set; }
    }

    /// <summary>Where a GPO is linked (from its report's LinksTo section).</summary>
    public sealed class GpoLinkLocation
    {
        public string SomPath { get; set; }
        public bool Enabled { get; set; }
        public bool NoOverride { get; set; }
    }

    public sealed class GpoPermission
    {
        public string Trustee { get; set; }
        public string TrusteeType { get; set; }
        /// <summary>GpoApply, GpoRead, GpoEdit, GpoEditDeleteModifySecurity, GpoCustom, None.</summary>
        public string Permission { get; set; }
        public bool Denied { get; set; }
    }

    public sealed class GpUpdateResult
    {
        public string Computer { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Everything the engine needs from Active Directory / Group Policy. The production implementation
    /// calls the in-box GroupPolicy and ActiveDirectory modules with -Server pinned to one DC; tests use
    /// an in-memory fake. Implementations must throw on failure (the engine records the error).
    /// </summary>
    public interface IGpoDirectory
    {
        /// <summary>The DC every call is pinned to.</summary>
        string Server { get; }

        DomainInfo GetDomain();
        bool ContainerExists(string dn);
        bool GroupExists(string name);

        List<GpoInfo> GetAllGpos();
        GpoInfo GetGpoByName(string name);
        string GetGpoReportXml(string gpoId);
        List<GpoLinkLocation> GetGpoLinkLocations(string gpoId);

        /// <summary>Links directly on the container (not inherited), ordered by link order.</summary>
        List<LinkInfo> GetLinks(string targetDn);
        void NewLink(string gpoId, string targetDn, int order, bool enabled);
        void SetLinkEnabled(string gpoId, string targetDn, bool enabled);
        void SetLinkOrder(string gpoId, string targetDn, int order);
        void RemoveLink(string gpoId, string targetDn);

        /// <summary>Creates a NEW GPO named <paramref name="targetName"/> from a backup. Must fail if the name exists.</summary>
        GpoInfo ImportNewGpo(string backupRoot, string backupId, string targetName);
        void SetGpoDescription(string gpoId, string description);
        /// <summary>Backup-GPO -All; returns the backup folder.</summary>
        void BackupAllGpos(string folder);
        void BackupGpo(string gpoId, string folder);
        void RemoveGpo(string gpoId);

        List<GpoPermission> GetPermissions(string gpoId);
        /// <summary>Set-GPPermission -Replace. Level "None" removes the trustee's permission.</summary>
        void SetPermission(string gpoId, string trustee, string trusteeType, string level);

        List<string> GetComputersUnder(string targetDn);
        /// <summary>DNs of every OU (plus the Domain Controllers OU) in the domain, for setup and pickers.</summary>
        List<string> GetOrganizationalUnits();
        /// <summary>Names of security groups whose name contains the text (case-insensitive).</summary>
        List<string> FindGroups(string nameContains);
        GpUpdateResult InvokeGpUpdate(string computer);
    }
}
