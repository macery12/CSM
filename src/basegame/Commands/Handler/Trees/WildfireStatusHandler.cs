using CSM.API.Commands;
using CSM.BaseGame.Commands.Data.Trees;
using CSM.BaseGame.Helpers;

namespace CSM.BaseGame.Commands.Handler.Trees
{
    public class WildfireStatusHandler : CommandHandler<WildfireStatusCommand>
    {
        public WildfireStatusHandler()
        {
            RelayOnServer = false;
        }

        protected override void Handle(WildfireStatusCommand command)
        {
            WildfireSync.Receive(command);
        }
    }

    public class RequestWildfireStatusHandler : CommandHandler<RequestWildfireStatusCommand>
    {
        public RequestWildfireStatusHandler()
        {
            RelayOnServer = false;
        }

        protected override void Handle(RequestWildfireStatusCommand command)
        {
            WildfireSync.Answer(command);
        }
    }
}
