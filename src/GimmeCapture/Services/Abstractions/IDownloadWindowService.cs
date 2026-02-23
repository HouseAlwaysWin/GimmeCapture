using Avalonia.Controls;

namespace GimmeCapture.Services.Abstractions;

public interface IDownloadWindowService
{
    void Update(Window owner, object? dataContext, bool isProcessing);

    void Close();
}
