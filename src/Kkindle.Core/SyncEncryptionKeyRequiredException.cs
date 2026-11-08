namespace Kkindle.Core;

public sealed class SyncEncryptionKeyRequiredException() : InvalidOperationException(
    "请先在同步设置中设置同步密钥，再导入或导出数据。请自行牢记密钥；密钥不会上传，遗忘后无法解密已有数据。");
