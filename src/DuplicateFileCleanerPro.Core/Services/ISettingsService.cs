using DuplicateFileCleanerPro.Core.Models;

namespace DuplicateFileCleanerPro.Core.Services;

public interface ISettingsService
{
    ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
