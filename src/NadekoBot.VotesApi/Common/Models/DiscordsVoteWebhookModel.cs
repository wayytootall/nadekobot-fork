﻿namespace NadekoBot.VotesApi
{
    public class DiscordsVoteWebhookModel
    {
        /// <summary>
        /// The ID of the user who voted
        /// </summary>
        public string User { get; set; }

        /// <summary>
        /// The ID of the bot which recieved the vote
        /// </summary>
        public string Bot { get; set; }

        /// <summary>
        /// The type of event, whether it is a vote event or test event
        /// </summary>
        public string Type { get; set; }
    }
}