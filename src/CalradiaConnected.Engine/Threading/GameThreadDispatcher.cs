using System;
using System.Collections.Concurrent;

namespace CalradiaConnected.Engine.Threading
{
    public interface IEngineDispatcher
    {
        void Enqueue(Action action);
        void ExecutePending(int maxActionsPerTick = 100);
    }

    public class GameThreadDispatcher : IEngineDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        public void Enqueue(Action action)
        {
            if (action == null) return;
            _queue.Enqueue(action);
        }

        public void ExecutePending(int maxActionsPerTick = 100)
        {
            int executed = 0;
            while (executed < maxActionsPerTick && _queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[CalradiaConnected] Dispatcher Error: {ex.Message}");
                }
                executed++;
            }
        }
    }
}

