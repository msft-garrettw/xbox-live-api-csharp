using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Microsoft.Xbox.Services.Multiplayer
{
    public enum MultiplayerSessionRestriction : int
    {
        Unknown = 0,
        None = 1,
        Local = 2,
        Followed = 3,
    }

}