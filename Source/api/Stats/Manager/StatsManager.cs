// Copyright (c) Microsoft Corporation
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// 
namespace Microsoft.Xbox.Services.Stats.Manager
{
    using global::System;
    using global::System.Collections.Generic;
    using global::System.Linq;

    using Microsoft.Xbox.Services.Shared;
    using Microsoft.Xbox.Services.Leaderboard;
    using global::System.Threading.Tasks;

    public class StatsManager : IStatsManager
    {
        private class StatsUserContext
        {
            public StatsValueDocument statsValueDocument;
            public XboxLiveContext xboxLiveContext;
            public StatsService statsService;
            public XboxLiveUser user;
        }

        private static IStatsManager instance;
        private static readonly TimeSpan TimePerCall = new TimeSpan(0, 0, 5);
        private readonly Dictionary<string, StatsUserContext> userStatContextMap;
        private readonly List<StatEvent> eventList;
        private readonly CallBufferTimer statTimer;
        private readonly CallBufferTimer statPriorityTimer;

        private void CheckUserValid(XboxLiveUser user)
        {
            if (user == null || user.XboxUserId == null || !this.userStatContextMap.ContainsKey(user.XboxUserId))
            {
                throw new ArgumentException("user");
            }
        }

        public static IStatsManager Singleton
        {
            get
            {
                return instance ?? (instance = XboxLiveContext.UseMockServices ? new MockStatsManager() : (IStatsManager)new StatsManager());
            }
        }

        private StatsManager()
        {
            this.userStatContextMap = new Dictionary<string, StatsUserContext>();
            this.eventList = new List<StatEvent>();

            this.statTimer = new CallBufferTimer(TimePerCall);
            this.statTimer.TimerCompleteEvent += this.CallBufferTimerCallback;

            this.statPriorityTimer = new CallBufferTimer(TimePerCall);
            this.statPriorityTimer.TimerCompleteEvent += this.CallBufferTimerCallback;
        }

        public void AddLocalUser(XboxLiveUser user)
        {
            if (user == null)
            {
                throw new ArgumentException("user");
            }

            string xboxUserId = user.XboxUserId;
            if (this.userStatContextMap.ContainsKey(xboxUserId))
            {
                throw new ArgumentException("User already in map");
            }

            var context = new StatsUserContext();
            this.userStatContextMap.Add(xboxUserId, context);

            var xboxLiveContext = new XboxLiveContext(user);
            var statsService = new StatsService(xboxLiveContext);

            context.xboxLiveContext = xboxLiveContext;
            context.statsService = statsService;
            context.user = user;
            context.statsValueDocument = new StatsValueDocument(null);

            statsService.GetStatsValueDocument().ContinueWith(statsValueDocTask =>
            {
                lock (this.userStatContextMap)
                {
                    if (user.IsSignedIn)
                    {
                        if (statsValueDocTask.IsCompleted)
                        {
                            if (this.userStatContextMap.ContainsKey(xboxUserId))
                            {
                                this.userStatContextMap[xboxUserId].statsValueDocument = statsValueDocTask.Result;
                                this.userStatContextMap[xboxUserId].statsValueDocument.FlushEvent += (sender, e) =>
                                {
                                    if (this.userStatContextMap.ContainsKey(xboxUserId))
                                    {
                                        this.FlushToService(this.userStatContextMap[xboxUserId]);
                                    }
                                };
                            }
                        }
                    }
                }

                this.AddEvent(new StatEvent(StatEventType.LocalUserAdded, user, statsValueDocTask.Exception, new StatEventArgs()));
            });
        }

        public void RemoveLocalUser(XboxLiveUser user)
        {
            this.CheckUserValid(user);
            var xboxUserId = user.XboxUserId;
            var svd = this.userStatContextMap[xboxUserId].statsValueDocument;
            if (svd.IsDirty)
            {
                svd.DoWork();
                //var serializedSVD = svd.Serialize();  // write offline
                this.userStatContextMap[xboxUserId].statsService.UpdateStatsValueDocument(svd).ContinueWith((continuationTask) =>
                {
                    if (this.ShouldWriteOffline(continuationTask.Exception))
                    {
                        // write offline
                    }

                    this.AddEvent(new StatEvent(StatEventType.LocalUserRemoved, user, continuationTask.Exception, new StatEventArgs()));
                });
            }
            else
            {
                this.AddEvent(new StatEvent(StatEventType.LocalUserRemoved, user, null, new StatEventArgs()));
            }

            this.userStatContextMap.Remove(xboxUserId);
        }

        public StatValue GetStat(XboxLiveUser user, string statName)
        {
            this.CheckUserValid(user);
            if (statName == null)
            {
                throw new ArgumentException("statName");
            }

            return this.userStatContextMap[user.XboxUserId].statsValueDocument.GetStat(statName);
        }

        public List<string> GetStatNames(XboxLiveUser user)
        {
            this.CheckUserValid(user);
            return this.userStatContextMap[user.XboxUserId].statsValueDocument.GetStatNames();
        }

        public void SetStatAsNumber(XboxLiveUser user, string statName, double value)
        {
            this.CheckUserValid(user);

            if (statName == null)
            {
                throw new ArgumentException("statName");
            }

            this.userStatContextMap[user.XboxUserId].statsValueDocument.SetStat(statName, value);
            RequestFlushToService(user);
        }

        public void SetStatAsInteger(XboxLiveUser user, string statName, Int64 value)
        {
            this.CheckUserValid(user);

            if (statName == null)
            {
                throw new ArgumentException("statName");
            }

            this.userStatContextMap[user.XboxUserId].statsValueDocument.SetStat(statName, value);
            RequestFlushToService(user);
        }

        public void SetStatAsString(XboxLiveUser user, string statName, string value)
        {
            this.CheckUserValid(user);

            if (statName == null)
            {
                throw new ArgumentException("statName");
            }

            this.userStatContextMap[user.XboxUserId].statsValueDocument.SetStat(statName, value);
            RequestFlushToService(user);
        }

        public void RequestFlushToService(XboxLiveUser user, bool isHighPriority = false)
        {
            this.CheckUserValid(user);
            this.userStatContextMap[user.XboxUserId].statsValueDocument.DoWork();

            List<string> userVec = new List<string>(1);
            userVec.Add(user.XboxUserId);

            if (isHighPriority)
            {
                this.statPriorityTimer.Fire(userVec);
            }
            else
            {
                this.statTimer.Fire(userVec);
            }
        }

        public List<StatEvent> DoWork()
        {
            lock (this.userStatContextMap)
            {
                var copyList = this.eventList.ToList();

                foreach (var userContextPair in this.userStatContextMap)
                {
                    userContextPair.Value.statsValueDocument.DoWork();
                }

                this.eventList.Clear();
                return copyList;
            }
        }

        private bool ShouldWriteOffline(AggregateException exception)
        {
            return false; // offline not implemented yet
        }

        private void FlushToService(StatsUserContext statsUserContext)
        {
            //var serializedSVD = statsUserContext.statsValueDocument.Serialize();
            statsUserContext.statsService.UpdateStatsValueDocument(statsUserContext.statsValueDocument).ContinueWith((continuationTask) =>
            {
#if WINDOWS_UWP
                if(continuationTask.IsFaulted)
#else
                if (continuationTask.Exception == null) // TODO: fix
#endif
                {
                    if (this.ShouldWriteOffline(continuationTask.Exception))
                    {
                        //WriteOffline(statsUserContext, serializedSVD);    // todo: add offline support
                    }
                    else
                    {
                        // log error
                    }
                }

                this.AddEvent(new StatEvent(StatEventType.StatUpdateComplete, statsUserContext.user, continuationTask.Exception, new StatEventArgs()));
            });
        }

        internal void AddEvent(StatEvent statEvent)
        {
            lock (this.eventList)
            {
                this.eventList.Add(statEvent);
            }
        }

        private void CallBufferTimerCallback(object caller, CallBufferReturnObject returnObject)
        {
            if (returnObject.UserList.Count != 0)
            {
                this.FlushToServiceCallback(returnObject.UserList[0]);
            }
        }

        private void FlushToServiceCallback(string userXuid)
        {
            if (this.userStatContextMap.ContainsKey(userXuid))
            {
                var statsUserContext = this.userStatContextMap[userXuid];
                var userSVD = statsUserContext.statsValueDocument;
                if (userSVD.IsDirty)
                {
                    userSVD.DoWork();
                    userSVD.ClearDirtyState();
                    this.FlushToService(statsUserContext);
                }
            }
        }

        public void GetLeaderboard(XboxLiveUser user, string statName, LeaderboardQuery query)
        {
            this.CheckUserValid(user);
            this.userStatContextMap[user.XboxUserId].xboxLiveContext.LeaderboardService.GetLeaderboardAsync(statName, query).ContinueWith(responseTask =>
            {
                ((StatsManager)Singleton).AddEvent(
                    new StatEvent(StatEventType.GetLeaderboardComplete, 
                    user, 
                    responseTask.Exception, 
                    new LeaderboardResultEventArgs(responseTask.Result)
                    ));
            });
        }

        public void GetSocialLeaderboard(XboxLiveUser user, string statName, string socialGroup, LeaderboardQuery query)
        {
            this.CheckUserValid(user);
            this.userStatContextMap[user.XboxUserId].xboxLiveContext.LeaderboardService.GetSocialLeaderboardAsync(statName, socialGroup, query).ContinueWith(responseTask =>
            {
                ((StatsManager)Singleton).AddEvent(
                    new StatEvent(StatEventType.GetLeaderboardComplete,
                    user,
                    responseTask.Exception,
                    new LeaderboardResultEventArgs(responseTask.Result)
                    ));
            });

        }
    }
}