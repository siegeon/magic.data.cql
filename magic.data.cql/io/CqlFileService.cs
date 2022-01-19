/*
 * Magic Cloud, copyright Aista, Ltd. See the attached LICENSE file for details.
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Cassandra;
using magic.node.contracts;
using magic.node.extensions;
using magic.data.cql.helpers;

namespace magic.data.cql.io
{
    /// <summary>
    /// File service for Magic storing files in ScyllaDB.
    /// </summary>
    public class CqlFileService : IFileService
    {
        readonly IConfiguration _configuration;
        readonly IRootResolver _rootResolver;

        /// <summary>
        /// Creates an instance of your type.
        /// </summary>
        /// <param name="configuration">Configuration needed to retrieve connection settings to ScyllaDB.</param>
        /// <param name="rootResolver">Needed to resolve client and cloudlet.</param>
        public CqlFileService(IConfiguration configuration, IRootResolver rootResolver)
        {
            _configuration = configuration;
            _rootResolver = rootResolver;
        }

        /// <inheritdoc />
        public void Copy(string source, string destination)
        {
            CopyAsync(source, destination).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public async Task CopyAsync(string source, string destination)
        {
            // Creating our session.
            using (var session = CreateSession(_configuration))
            {
                // Making sure destination folder exists.
                var relDest = BreakDownPath(_rootResolver.RelativePath(destination));
                if (!await CqlFolderService.FolderExists(session, _rootResolver.DynamicFiles, relDest.Folder))
                    throw new HyperlambdaException("Destination folder doesn't exist");

                // Reading content from source file.
                var content = await GetFileContent(session, _rootResolver, source);

                // Saving content to destination file.
                await SaveAsync(session, _rootResolver.DynamicFiles, destination, content);
            }
        }

        /// <inheritdoc />
        public void Delete(string path)
        {
            DeleteAsync(path).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string path)
        {
            // Creating our session.
            using (var session = CreateSession(_configuration))
            {
                // Deleting file.
                var cql = "delete from files where cloudlet = :cloudlet and folder = :folder and filename = :filename";
                var relPath = BreakDownPath(_rootResolver.RelativePath(path));
                var args = new Dictionary<string, object>
                {
                    { "cloudlet", _rootResolver.DynamicFiles },
                    { "folder", relPath.Folder },
                    { "filename", relPath.File },
                };
                await session.ExecuteAsync(new SimpleStatement(args, cql));
            }
        }

        /// <inheritdoc />
        public bool Exists(string path)
        {
            return ExistsAsync(path).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string path)
        {
            // Creating our session.
            using (var session = CreateSession(_configuration))
            {
                // Figuring out if file exists.
                var cql = "select filename from files where cloudlet = :cloudlet and folder = :folder and filename = :filename";
                var fullPath = BreakDownPath(_rootResolver.RelativePath(path));
                var args = new Dictionary<string, object>
                {
                    { "cloudlet", _rootResolver.DynamicFiles },
                    { "folder", fullPath.Folder },
                    { "filename", fullPath.File },
                };
                var rs = await session.ExecuteAsync(new SimpleStatement(args, cql));
                var row = rs.FirstOrDefault();
                return row == null ? false : true;
            }
        }

        /// <inheritdoc />
        public List<string> ListFiles(string folder, string extension = null)
        {
            return ListFilesAsync(folder, extension).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public async Task<List<string>> ListFilesAsync(string folder, string extension = null)
        {
            var relPath = BreakDownPath(_rootResolver.RelativePath(folder));
            using (var session = CreateSession(_configuration))
            {
                // Sanity checking invocation.
                if (!await CqlFolderService.FolderExists(session, _rootResolver.DynamicFiles, relPath.Folder))
                    throw new HyperlambdaException("Folder doesn't exist");

                var cql = "select filename from files where cloudlet = :cloudlet and folder = :folder";
                var args = new Dictionary<string, object>
                {
                    { "cloudlet", _rootResolver.DynamicFiles },
                    { "folder", relPath.Folder },
                };
                var rs = await session.ExecuteAsync(new SimpleStatement(args, cql));
                var result = new List<string>();
                foreach (var idx in rs.GetRows())
                {
                    var idxFile = idx.GetValue<string>("filename");
                    if (idxFile != "" && (extension == null || idxFile.EndsWith(extension)))
                    {
                        result.Add(_rootResolver.DynamicFiles + relPath.Folder.Substring(1) + idxFile);
                    }
                }
                return result;
            }
        }

        /// <inheritdoc />
        public string Load(string path)
        {
            return LoadAsync(path).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public async Task<string> LoadAsync(string path)
        {
            using (var session = CreateSession(_configuration))
            {
                var cql = "select content from files where cloudlet = :cloudlet and folder = :folder and filename = :filename";
                var fullPath = _rootResolver.RelativePath(path);
                var args = new Dictionary<string, object>
                {
                    { "cloudlet", _rootResolver.DynamicFiles },
                    { "folder", fullPath.Substring(0, fullPath.LastIndexOf('/') + 1) },
                    { "filename", fullPath.Substring(fullPath.LastIndexOf('/') + 1) },
                };
                var rs = await session.ExecuteAsync(new SimpleStatement(args, cql));
                var row = rs.FirstOrDefault() ?? throw new HyperlambdaException("No such file found");
                return row.GetValue<string>("content");
            }
        }

        /// <inheritdoc />
        public byte[] LoadBinary(string path)
        {
            return LoadBinaryAsync(path).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public async Task<byte[]> LoadBinaryAsync(string path)
        {
            return Convert.FromBase64String(await LoadAsync(path));
        }

        /// <inheritdoc />
        public void Move(string source, string destination)
        {
            MoveAsync(source, destination).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public async Task MoveAsync(string source, string destination)
        {
            using (var session = CreateSession(_configuration))
            {
                // Sanity checking invocation.
                var destinationFolder = _rootResolver.RelativePath(destination);
                destinationFolder = destinationFolder.Substring(0, destinationFolder.LastIndexOf("/") + 1);
                if (!await CqlFolderService.FolderExists(
                    session,
                    _rootResolver.DynamicFiles,
                    destinationFolder.Substring(0, destinationFolder.LastIndexOf("/") + 1)))
                    throw new HyperlambdaException("Destination folder doesn't exist");

                var cql = "select content from files where cloudlet = :cloudlet and folder = :folder and filename = :filename";
                var fullPath = _rootResolver.RelativePath(source);
                var args = new Dictionary<string, object>
                {
                    { "cloudlet", _rootResolver.DynamicFiles },
                    { "folder", fullPath.Substring(0, fullPath.LastIndexOf('/') + 1) },
                    { "filename", fullPath.Substring(fullPath.LastIndexOf('/') + 1) },
                };
                var rs = await session.ExecuteAsync(new SimpleStatement(args, cql));
                var row = rs.FirstOrDefault() ?? throw new HyperlambdaException("No such source file");
                var content = row.GetValue<string>("content");
                await SaveAsync(
                    session,
                    _rootResolver.DynamicFiles,
                    destination,
                    content);
                cql = "delete from files where cloudlet = :cloudlet and folder = :folder and filename = :filename";
                await session.ExecuteAsync(new SimpleStatement(args, cql));
            }
        }

        /// <inheritdoc />
        public void Save(string path, string content)
        {
            SaveAsync(path, content).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public void Save(string path, byte[] content)
        {
            SaveAsync(path, content).GetAwaiter().GetResult();
        }

        /// <inheritdoc />
        public async Task SaveAsync(string path, string content)
        {
            using (var session = CreateSession(_configuration))
            {
                // Sanity checking invocation.
                var destinationFolder = _rootResolver.RelativePath(path);
                destinationFolder = destinationFolder.Substring(0, destinationFolder.LastIndexOf("/") + 1);
                if (!await CqlFolderService.FolderExists(
                    session,
                    _rootResolver.DynamicFiles,
                    destinationFolder))
                    throw new HyperlambdaException("Destination folder doesn't exist");

                await SaveAsync(
                    session,
                    _rootResolver.DynamicFiles,
                    path,
                    content);
            }
        }

        /// <inheritdoc />
        public async Task SaveAsync(string path, byte[] content)
        {
            await SaveAsync(path, Convert.ToBase64String(content));
        }

        #region [ -- Internal helper methods -- ]

        /*
         * Creates a ScyllaDB session and returns to caller.
         */
        internal static ISession CreateSession(IConfiguration configuration, string db = "magic")
        {
            var cluster = Cluster.Builder()
                .AddContactPoints(configuration["magic:cql:host"] ?? "127.0.0.1")
                .Build();
            return cluster.Connect(db);
        }

        /*
         * Returns the content of the specified file to caller.
         */
        internal static async Task<string> GetFileContent(
            ISession session,
            IRootResolver rootResolver,
            string path)
        {
            var rel = BreakDownPath(rootResolver.RelativePath(path));
            var rs = await Utilities.SingleAsync(
                session,
                "select content from files where cloudlet = :cloudlet and folder = :folder and filename = :filename",
                ("cloudlet", rootResolver.DynamicFiles),
                ("folder", rel.Folder),
                ("filename", rel.File));
            return rs?.GetValue<string>("content") ?? throw new HyperlambdaException("No such file");
        }

        #endregion

        #region [ -- Private helper methods -- ]

        /*
         * Common helper method to save file on specified session given specified client and cloudlet ID.
         */
        async Task SaveAsync(
            ISession session,
            string cloudlet,
            string path,
            string content)
        {
            var cql = "insert into files (cloudlet, folder, filename, content) values (:cloudlet, :folder, :filename, :content)";
            var destPath = BreakDownPath(_rootResolver.RelativePath(path));
            var args = new Dictionary<string, object>
            {
                { "cloudlet", cloudlet },
                { "folder", destPath.Folder },
                { "filename", destPath.File },
                { "content", content },
            };
            await session.ExecuteAsync(new SimpleStatement(args, cql));
        }

        /*
         * Breaks down the specified path into its folder value and its file value.
         */
        static (string Folder, string File) BreakDownPath(string path)
        {
            return (path.Substring(0, path.LastIndexOf('/') + 1), path.Substring(path.LastIndexOf('/') + 1));
        }

        #endregion
    }
}