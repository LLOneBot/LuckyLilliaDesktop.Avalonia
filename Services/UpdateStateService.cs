using System;
using System.Reactive.Subjects;

namespace LuckyLilliaDesktop.Services;

public class UpdateState
{
    public bool AppHasUpdate { get; set; }
    public string AppLatestVersion { get; set; } = "";
    public string AppReleaseUrl { get; set; } = "";

    public bool PmhqHasUpdate { get; set; }
    public string PmhqLatestVersion { get; set; } = "";
    public string PmhqReleaseUrl { get; set; } = "";

    public bool LLBotHasUpdate { get; set; }
    public string LLBotLatestVersion { get; set; } = "";
    public string LLBotReleaseUrl { get; set; } = "";

    public bool HasAnyUpdate => AppHasUpdate || PmhqHasUpdate || LLBotHasUpdate;
    public bool IsChecked { get; set; }
}

public interface IUpdateStateService
{
    UpdateState State { get; }
    IObservable<UpdateState> StateChanged { get; }
    void UpdateState(UpdateState state);
    void ClearUpdate(string component);
}

public class UpdateStateService : IUpdateStateService
{
    private readonly BehaviorSubject<UpdateState> _stateSubject = new(new UpdateState());

    public UpdateState State => _stateSubject.Value;
    public IObservable<UpdateState> StateChanged => _stateSubject;

    public void UpdateState(UpdateState state)
    {
        _stateSubject.OnNext(state);
    }

    public void ClearUpdate(string component)
    {
        var state = State;
        switch (component.ToLower())
        {
            case "app":
            case "管理器":
                state.AppHasUpdate = false;
                break;
            case "pmhq":
                state.PmhqHasUpdate = false;
                break;
            case "llbot":
                state.LLBotHasUpdate = false;
                break;
        }
        _stateSubject.OnNext(state);
    }
}
