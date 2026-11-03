namespace HeThongHoTroChoThueThietBi.Common.FileStorage.Models;

public enum StorageFolder
{
    Equipment,
    Company,
    Contract,
    Handover,
    Incident,
    Maintenance,
    UserAvatar
}

public static class StorageFolderExtensions
{
    public static string ToFolderName(this StorageFolder folder)
    {
        return folder switch
        {
            StorageFolder.Equipment => "equipment",
            StorageFolder.Company => "company",
            StorageFolder.Contract => "contract",
            StorageFolder.Handover => "handover",
            StorageFolder.Incident => "incident",
            StorageFolder.Maintenance => "maintenance",
            StorageFolder.UserAvatar => "user-avatar",

            _ => throw new ArgumentOutOfRangeException(
                nameof(folder),
                folder,
                "Folder không được hỗ trợ."
            )
        };
    }
}