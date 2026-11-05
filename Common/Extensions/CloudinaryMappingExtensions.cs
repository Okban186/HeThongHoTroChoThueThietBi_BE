using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Models;

namespace HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;

public static class CloudinaryMappingExtensions
{

    public static ResourceType ToCloudinaryNetResourceType(this CloudinaryResourceType resourceType)
    {
        return resourceType switch
        {
            CloudinaryResourceType.Image => ResourceType.Image,
            CloudinaryResourceType.Video => ResourceType.Video,
            CloudinaryResourceType.Raw => ResourceType.Raw,
            _ => ResourceType.Image
        };
    }
    public static CloudinaryResourceType ToInternalResourceType(this ResourceType resourceType)
    {
        return resourceType switch
        {
            ResourceType.Image => CloudinaryResourceType.Image,
            ResourceType.Video => CloudinaryResourceType.Video,
            ResourceType.Raw => CloudinaryResourceType.Raw,
            _ => CloudinaryResourceType.Image
        };
    }
}