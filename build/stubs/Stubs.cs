// Compile-check stubs mirroring jellyfin/jellyfin release-12.z public signatures.
// NOT shipped. See JellyfinStubs.csproj for why these exist.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Data.Enums
{
    public enum BaseItemKind { Audio, Episode, Movie, MusicVideo, Series, Season, Video, Folder }
    public enum MediaType { Unknown, Video, Audio, Photo, Book }
    public enum CollectionTypeOptions { unknown, movies, tvshows, music, musicvideos, homevideos, boxsets, books, mixed }
}

namespace MediaBrowser.Model.Entities
{
    using Jellyfin.Data.Enums;

    public class VirtualFolderInfo
    {
        public string Name { get; set; }
        public string[] Locations { get; set; } = Array.Empty<string>();
        public CollectionTypeOptions? CollectionType { get; set; }
        public string ItemId { get; set; }
    }
}

namespace MediaBrowser.Model.Querying
{
    public class QueryResult<T>
    {
        public QueryResult() { Items = Array.Empty<T>(); }
        public IReadOnlyList<T> Items { get; set; }
        public int TotalRecordCount { get; set; }
        public int StartIndex { get; set; }
    }
}

namespace MediaBrowser.Model.Entities
{
    public enum MediaStreamType { Audio, Video, Subtitle, EmbeddedImage, Data, Lyric }

    public class MediaStream
    {
        public string Codec { get; set; }
        public string Language { get; set; }
        public string Title { get; set; }
        public string Profile { get; set; }
        public string Path { get; set; }
        public string ChannelLayout { get; set; }
        public int? BitRate { get; set; }
        public int? Channels { get; set; }
        public int? SampleRate { get; set; }
        public bool IsDefault { get; set; }
        public bool IsForced { get; set; }
        public bool IsExternal { get; set; }
        public bool IsHearingImpaired { get; set; }
        public MediaStreamType Type { get; set; }
        public int Index { get; set; }
        public string DisplayTitle { get; set; }
    }
}

namespace MediaBrowser.Model.IO
{
    public class FileSystemMetadata
    {
        public string FullName { get; set; }
        public string Name { get; set; }
        public bool Exists { get; set; }
        public bool IsDirectory { get; set; }
        public long Length { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }

    public interface IFileSystem
    {
        FileSystemMetadata GetFileSystemInfo(string path);
        FileSystemMetadata GetFileInfo(string path);
        FileSystemMetadata GetDirectoryInfo(string path);
        bool DirectoryExists(string path);
        bool FileExists(string path);
    }
}

namespace MediaBrowser.Model.Serialization
{
    public interface IXmlSerializer
    {
        object DeserializeFromStream(Type type, Stream stream);
        void SerializeToStream(object obj, Stream stream);
        void SerializeToFile(object obj, string file);
        object DeserializeFromFile(Type type, string file);
        object DeserializeFromBytes(Type type, byte[] buffer);
    }
}

namespace MediaBrowser.Model.Plugins
{
    public class BasePluginConfiguration { }

    public class PluginPageInfo
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; }
        public string EmbeddedResourcePath { get; set; } = string.Empty;
        public bool EnableInMainMenu { get; set; }
        public string MenuSection { get; set; }
        public string MenuIcon { get; set; }
    }

    public interface IHasWebPages
    {
        IEnumerable<PluginPageInfo> GetPages();
    }

    public class PluginInfo
    {
        public PluginInfo(string name, Version version, string description, Guid id, bool canUninstall)
        {
            Name = name; Version = version; Description = description; Id = id; CanUninstall = canUninstall;
        }
        public string Name { get; set; }
        public Version Version { get; set; }
        public string Description { get; set; }
        public Guid Id { get; set; }
        public bool CanUninstall { get; set; }
        public string ConfigurationFileName { get; set; }
    }
}

namespace MediaBrowser.Model.Tasks
{
    public enum TaskTriggerInfoType { DailyTrigger, WeeklyTrigger, IntervalTrigger, StartupTrigger }

    public class TaskTriggerInfo
    {
        public TaskTriggerInfoType Type { get; set; }
        public long? TimeOfDayTicks { get; set; }
        public long? IntervalTicks { get; set; }
        public DayOfWeek? DayOfWeek { get; set; }
        public long? MaxRuntimeTicks { get; set; }
    }

    public interface IScheduledTask
    {
        string Name { get; }
        string Key { get; }
        string Description { get; }
        string Category { get; }
        Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken);
        IEnumerable<TaskTriggerInfo> GetDefaultTriggers();
    }

    public interface IConfigurableScheduledTask
    {
        bool IsHidden { get; }
        bool IsEnabled { get; }
        bool IsLogged { get; }
    }
}

namespace MediaBrowser.Common.Configuration
{
    public interface IApplicationPaths
    {
        string ProgramDataPath { get; }
        string WebPath { get; }
        string ProgramSystemPath { get; }
        string DataPath { get; }
        string ImageCachePath { get; }
        string PluginsPath { get; }
        string PluginConfigurationsPath { get; }
        string LogDirectoryPath { get; }
        string ConfigurationDirectoryPath { get; }
        string SystemConfigurationFilePath { get; }
        string CachePath { get; }
        string TempDirectory { get; }
    }
}

namespace MediaBrowser.Common.Api
{
    public static class Policies
    {
        public const string FirstTimeSetupOrElevated = "FirstTimeSetupOrElevated";
        public const string RequiresElevation = "RequiresElevation";
    }
}

namespace MediaBrowser.Common.Plugins
{
    using MediaBrowser.Common.Configuration;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Serialization;

    public interface IPlugin
    {
        string Name { get; }
        string Description { get; }
        Guid Id { get; }
        Version Version { get; }
        string AssemblyFilePath { get; }
        string DataFolderPath { get; }
        bool CanUninstall { get; }
        PluginInfo GetPluginInfo();
        void OnUninstalling();
    }

    public interface IPluginAssembly { }

    public interface IHasPluginConfiguration
    {
        Type ConfigurationType { get; }
        BasePluginConfiguration Configuration { get; }
        void UpdateConfiguration(BasePluginConfiguration configuration);
    }

    public abstract class BasePlugin : IPlugin, IPluginAssembly
    {
        public abstract string Name { get; }
        public virtual string Description => string.Empty;
        public virtual Guid Id { get; private set; }
        public Version Version { get; private set; }
        public string AssemblyFilePath { get; private set; }
        public string DataFolderPath { get; private set; }
        public bool CanUninstall => true;
        public virtual PluginInfo GetPluginInfo() => new PluginInfo(Name, Version, Description, Id, CanUninstall);
        public virtual void OnUninstalling() { }
        public void SetAttributes(string assemblyFilePath, string dataFolderPath, Version assemblyVersion)
        {
            AssemblyFilePath = assemblyFilePath; DataFolderPath = dataFolderPath; Version = assemblyVersion;
        }
        public void SetId(Guid assemblyId) => Id = assemblyId;
    }

    public abstract class BasePlugin<TConfigurationType> : BasePlugin, IHasPluginConfiguration
        where TConfigurationType : BasePluginConfiguration
    {
        private TConfigurationType _configuration;

        protected BasePlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        {
            ApplicationPaths = applicationPaths;
            XmlSerializer = xmlSerializer;
        }

        protected IApplicationPaths ApplicationPaths { get; private set; }
        protected IXmlSerializer XmlSerializer { get; private set; }
        public Type ConfigurationType => typeof(TConfigurationType);
        public EventHandler<BasePluginConfiguration> ConfigurationChanged { get; set; }
        protected string AssemblyFileName => System.IO.Path.GetFileName(AssemblyFilePath);
        public TConfigurationType Configuration
        {
            get => _configuration ??= Activator.CreateInstance<TConfigurationType>();
            protected set => _configuration = value;
        }
        public virtual string ConfigurationFileName => "plugin.xml";
        public string ConfigurationFilePath => "plugin.xml";
        BasePluginConfiguration IHasPluginConfiguration.Configuration => Configuration;
        public virtual void SaveConfiguration(TConfigurationType config) { }
        public virtual void SaveConfiguration() { }
        public virtual void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            Configuration = (TConfigurationType)configuration;
            SaveConfiguration(Configuration);
            ConfigurationChanged?.Invoke(this, configuration);
        }
    }
}

namespace MediaBrowser.Controller
{
    public interface IServerApplicationHost { }
}

namespace MediaBrowser.Controller.Plugins
{
    public interface IPluginServiceRegistrator
    {
        void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost);
    }
}

namespace MediaBrowser.Controller.Providers
{
    using MediaBrowser.Model.IO;

    public interface IDirectoryService
    {
        FileSystemMetadata[] GetFileSystemEntries(string path);
        List<FileSystemMetadata> GetFiles(string path);
        FileSystemMetadata GetFile(string path);
        IReadOnlyList<string> GetFilePaths(string path);
        IReadOnlyList<string> GetFilePaths(string path, bool clearCache, bool sort = false);
    }

    public class DirectoryService : IDirectoryService
    {
        public DirectoryService(IFileSystem fileSystem) { }
        public FileSystemMetadata[] GetFileSystemEntries(string path) => Array.Empty<FileSystemMetadata>();
        public List<FileSystemMetadata> GetFiles(string path) => new List<FileSystemMetadata>();
        public FileSystemMetadata GetFile(string path) => null;
        public IReadOnlyList<string> GetFilePaths(string path) => Array.Empty<string>();
        public IReadOnlyList<string> GetFilePaths(string path, bool clearCache, bool sort = false) => Array.Empty<string>();
    }

    public enum MetadataRefreshMode { None = 0, ValidationOnly = 1, Default = 2, FullRefresh = 3 }

    public class ImageRefreshOptions
    {
        public ImageRefreshOptions(IDirectoryService directoryService) { DirectoryService = directoryService; }
        public MetadataRefreshMode ImageRefreshMode { get; set; }
        public IDirectoryService DirectoryService { get; }
        public bool ReplaceAllImages { get; set; }
        public bool IsAutomated { get; set; }
    }

    public class MetadataRefreshOptions : ImageRefreshOptions
    {
        public MetadataRefreshOptions(IDirectoryService directoryService) : base(directoryService)
        {
            MetadataRefreshMode = MetadataRefreshMode.Default;
        }
        public bool ReplaceAllMetadata { get; set; }
        public bool RegenerateTrickplay { get; set; }
        public MetadataRefreshMode MetadataRefreshMode { get; set; }
        public string[] RefreshPaths { get; set; }
        public bool ForceSave { get; set; }
        public bool EnableRemoteContentProbe { get; set; }
        public bool RemoveOldMetadata { get; set; }
    }
}

namespace MediaBrowser.Controller.Library
{
    using Jellyfin.Data.Enums;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Querying;

    public interface ILibraryManager
    {
        BaseItem GetItemById(Guid id);
        IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery query);
        QueryResult<BaseItem> GetItemsResult(InternalItemsQuery query);
        IReadOnlyList<Folder> GetUserRootFolder();
        List<VirtualFolderInfo> GetVirtualFolders();
        List<VirtualFolderInfo> GetVirtualFolders(bool includeRefreshState);
    }

    public interface IMediaSourceManager
    {
        IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId);
    }
}

namespace MediaBrowser.Controller.Entities
{
    using Jellyfin.Data.Enums;
    using MediaBrowser.Controller.Providers;

    public enum ItemUpdateType { None = 0, MetadataImport = 1, ImageUpdate = 2, MetadataDownload = 4, MetadataEdit = 8 }

    public class BaseItem
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public long? RunTimeTicks { get; set; }
        public Guid ParentId { get; set; }
        public virtual bool IsFolder => false;
        public virtual bool IsFileProtocol => true;
        public virtual string ContainingFolderPath => System.IO.Path.GetDirectoryName(Path);
        public virtual string FileNameWithoutExtension => System.IO.Path.GetFileNameWithoutExtension(Path);
        public DateTime DateCreated { get; set; }
        public DateTime DateModified { get; set; }
        public Task<ItemUpdateType> RefreshMetadata(MetadataRefreshOptions options, CancellationToken cancellationToken)
            => Task.FromResult(ItemUpdateType.None);
    }

    public class Folder : BaseItem { public override bool IsFolder => true; }

    public class Video : BaseItem
    {
        public string[] AudioFiles { get; set; } = Array.Empty<string>();
        public string[] SubtitleFiles { get; set; } = Array.Empty<string>();
    }

    public class InternalItemsQuery
    {
        public bool Recursive { get; set; }
        public MediaType[] MediaTypes { get; set; } = Array.Empty<MediaType>();
        public BaseItemKind[] IncludeItemTypes { get; set; } = Array.Empty<BaseItemKind>();
        public bool? IsVirtualItem { get; set; }
        public bool EnableTotalRecordCount { get; set; }
        public Guid[] ItemIds { get; set; } = Array.Empty<Guid>();
        public Guid[] AncestorIds { get; set; } = Array.Empty<Guid>();
        public int? Limit { get; set; }
    }
}

namespace MediaBrowser.Controller.MediaEncoding
{
    public interface IMediaEncoder
    {
        string EncoderPath { get; }
        string ProbePath { get; }
        Version EncoderVersion { get; }
        bool SupportsFilter(string filter);
    }
}

namespace MediaBrowser.Controller.Session
{
    using MediaBrowser.Controller.Entities;

    public class PlayerStateInfo
    {
        public bool IsPaused { get; set; }
        public long? PositionTicks { get; set; }
    }

    public class SessionInfo
    {
        public string Id { get; set; }
        public string DeviceName { get; set; }
        public PlayerStateInfo PlayState { get; set; } = new PlayerStateInfo();
        public BaseItem FullNowPlayingItem { get; set; }
        public object NowPlayingItem { get; set; }
    }

    public interface ISessionManager
    {
        IEnumerable<SessionInfo> Sessions { get; }
    }
}
