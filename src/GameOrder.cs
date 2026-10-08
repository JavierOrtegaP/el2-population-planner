using System;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Sandbox;

namespace PopulationPlanner
{
    // An order sent with the game's ticket (main thread). The game answers every one: Valid (carried out: it processes an
    // order when it validates it), Invalid (its own checks refused it) or Rejected (not taken in the state the game is in,
    // e.g. while it processes the turn). A Rejected order is sent again once the game is in another state, so the mod
    // never needs to guess how long to wait or how often to try.
    internal sealed class GameOrder
    {
        private readonly Func<Order> make;
        private PostOrderTicket ticket;
        private string sentIn;

        public GameOrder(Func<Order> make)
        {
            this.make = make;
            Send();
        }

        // The state version in which the mod first saw the game's answer: only a state taken after it shows the order's
        // effect for sure.
        public int AnsweredVersion { get; private set; } = -1;

        // Undefined while there is no answer yet (or a Rejected order waits for the game to move on), else the answer.
        public PostOrderResponse Check(int stateVersion)
        {
            if (ticket == null || !ticket.IsDone)
            {
                return PostOrderResponse.Undefined;
            }
            if (ticket.Result == PostOrderResponse.Rejected)
            {
                if (CurrentState() != sentIn)
                {
                    Send();
                }
                return PostOrderResponse.Undefined;
            }
            if (AnsweredVersion < 0)
            {
                AnsweredVersion = stateVersion;
            }
            return ticket.Result;
        }

        // An answer the mod has seen, and a state taken since then.
        public bool SeenSince(int stateVersion) => AnsweredVersion >= 0 && stateVersion > AnsweredVersion;

        private void Send()
        {
            sentIn = CurrentState();
            ticket = SandboxManager.PostAndTrackOrder(make());
            StateCapture.RequestRefresh();
        }

        private static string CurrentState() => SandboxManager.Sandbox?.CurrentStateName ?? string.Empty;
    }
}
