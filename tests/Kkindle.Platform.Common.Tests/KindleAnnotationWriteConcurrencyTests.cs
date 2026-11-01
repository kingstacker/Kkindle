using Xunit;
using System.Security.Cryptography;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.Common;
using Microsoft.Data.Sqlite;

namespace Kkindle.Platform.Common.Tests;
public sealed class KindleAnnotationWriteConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MountedWriterCommitsAtomicallyOrRejectsWal(bool wal)
    {
        var root = Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(Path.Combine(root, "app"));
            paths.EnsureDirectories();
            var deviceRoot = Path.Combine(root, "Kindle");
            var database = Path.Combine(deviceRoot, "system", "ksdk", ".annotations", "amzn1.account.TEST", "ksdk_annotation_v1.db");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            using (var connection = Open(database))
            {
                using var schema = connection.CreateCommand();
                schema.CommandText = """
                    CREATE TABLE nonsyncable_annotations(annotation_id TEXT NOT NULL, book_id TEXT NOT NULL,
                        dataset INTEGER, start_position TEXT, end_position TEXT, created_time INTEGER,
                        modified_time INTEGER, serialized_payload TEXT, PRIMARY KEY(annotation_id,book_id));
                    INSERT INTO nonsyncable_annotations VALUES('original','other-book',1,'{}','{}',1,1,'{}');
                    """;
                schema.ExecuteNonQuery();
                if (wal)
                {
                    schema.CommandText = "PRAGMA journal_mode=WAL";
                    schema.ExecuteScalar();
                }
            }
            var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database))).ToLowerInvariant();
            var snapshot = new KindleAnnotationSnapshot(Path.GetRelativePath(deviceRoot, database), originalHash, []);
            var service = new MassStorageKindleDeviceService(paths, new Metadata(), [root]);
            var device = new KindleDevice { RootPath = deviceRoot, VolumeSerial = "REVIEW", Transport = KindleTransport.MassStorage };
            var mutation = new KindleAnnotationMutation("added", "test-book", 1, 10, 20, 1, 2, "", "{}");
            if (wal)
            {
                await Assert.ThrowsAsync<IOException>(() => service.ApplyAnnotationMutationsAsync(device, snapshot, [mutation]));
                Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database))).ToLowerInvariant());
            }
            else
            {
                var result = await service.ApplyAnnotationMutationsAsync(device, snapshot, [mutation]);
                Assert.Equal(1, result.UpsertedRows);
                Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.LocalBackupPath!))).ToLowerInvariant());
                Assert.Equal(originalHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.DeviceBackupPath!))).ToLowerInvariant());
                Assert.Equal(result.NewSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database))).ToLowerInvariant());
                using var connection = Open(database);
                using var verify = connection.CreateCommand();
                verify.CommandText = "SELECT count(*) FROM nonsyncable_annotations";
                Assert.Equal(2L, verify.ExecuteScalar());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MountedWriteDoesNotDiscardChangesMadeWhileCandidateIsBuilt()
    {
        var root=Path.Combine(Path.GetTempPath(), "KkindleTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths=new AppPaths(Path.Combine(root,"app"));paths.EnsureDirectories();
            var deviceRoot=Path.Combine(root,"Kindle");
            var db=Path.Combine(deviceRoot,"system","ksdk",".annotations","amzn1.account.TEST","ksdk_annotation_v1.db");
            Directory.CreateDirectory(Path.GetDirectoryName(db)!);
            using(var connection=Open(db))
            {
                using var command=connection.CreateCommand();
                command.CommandText="CREATE TABLE nonsyncable_annotations(annotation_id TEXT NOT NULL,book_id TEXT NOT NULL,dataset INTEGER,start_position TEXT,end_position TEXT,created_time INTEGER,modified_time INTEGER,serialized_payload TEXT,PRIMARY KEY(annotation_id,book_id)); INSERT INTO nonsyncable_annotations VALUES('original','book',1,'{}','{}',1,1,'{}');";
                command.ExecuteNonQuery();
            }
            var sha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(db))).ToLowerInvariant();
            var snapshot=new KindleAnnotationSnapshot(Path.GetRelativePath(deviceRoot,db),sha,[]);
            var device=new KindleDevice{RootPath=deviceRoot,VolumeSerial="MOUNTED-REVIEW",Transport=KindleTransport.MassStorage};
            var service=new MassStorageKindleDeviceService(paths,new Metadata(),[root]);
            var mutations=Enumerable.Range(0,8000).Select(i=>new KindleAnnotationMutation("pc-"+i,"book",1,10,20,1,2,"","{}")).ToArray();
            var completed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher=new FileSystemWatcher(paths.Backups,"candidate.db"){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName};
            watcher.Created+=(_,e)=>
            {
                try
                {
                    using var connection=Open(db);
                    using var command=connection.CreateCommand();
                    command.CommandText="INSERT INTO nonsyncable_annotations VALUES('external-new','unrelated-book',1,'{}','{}',1,3,'{}');";
                    command.ExecuteNonQuery();
                    completed.TrySetResult(File.Exists(e.FullPath));
                }
                catch(Exception error){completed.TrySetException(error);}
            };
            watcher.EnableRaisingEvents=true;
            await Assert.ThrowsAsync<IOException>(() => Task.Run(()=>service.ApplyAnnotationMutationsAsync(device,snapshot,mutations)));
            Assert.True(await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)),"External change must finish before candidate is installed");
            using var verify=Open(db);
            using var query=verify.CreateCommand();query.CommandText="SELECT count(*) FROM nonsyncable_annotations WHERE annotation_id='external-new'";
            Assert.Equal(1L,(long)query.ExecuteScalar()!);
        }
        finally{Directory.Delete(root, recursive:true);}
    }
    private static SqliteConnection Open(string path)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());connection.Open();return connection;
    }
    private sealed class Metadata:IMetadataService
    {
        public Task<BookMetadata> ReadMetadataAsync(string path,CancellationToken cancellationToken=default)=>Task.FromResult(new BookMetadata{Title="Review",Authors="Author"});
    }
}
