using System;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Abstractions;

public interface ISettingsSaveCoordinatorFactory
{
    ISettingsSaveCoordinator Create(Func<Task<bool>> saveAsync);
}
