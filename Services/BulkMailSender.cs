﻿using MimeKit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using System.Dynamic;
using System.Diagnostics;

namespace MailDemon
{
    /// <summary>
    /// Handles sending of bulk email
    /// </summary>
    public interface IBulkMailSender
    {
        /// <summary>
        /// Send bulk email
        /// </summary>
        /// <param name="list">List to send email from</param>
        /// <param name="mailCreator">Creates the email message</param>
        /// <param name="mailSender">Sends the email message</param>
        /// <param name="viewBag">View bag</param>
        /// <param name="all">True to send to all subscribers, false to only send to subscribers with a non-empty result (error state)</param>
        /// <param name="fullTemplateName">The template to create, i.e. List@TemplateName</param>
        /// <param name="unsubscribeUrl">The unsubscribe url to put in the message, {0} is the unsubscribe token</param>
        /// <returns>Task</returns>
        Task SendBulkMail(MailList list, IMailCreator mailCreator, IMailSender mailSender, ExpandoObject viewBag, bool all,
            string fullTemplateName, string unsubscribeUrl);
    }

    public class BulkMailSender : IBulkMailSender
    {
        private readonly IMailDemonDatabaseProvider dbProvider;

        private async IAsyncEnumerable<MailToSend> GetMessages(IEnumerable<MailListSubscription> subs, IMailCreator mailCreator, MailList list,
            ExpandoObject viewBag, string fullTemplateName, Action<MailListSubscription, string> callback)
        {
            foreach (MailListSubscription sub in subs)
            {
                MimeMessage message;
                try
                {
                    message = await mailCreator.CreateMailAsync(fullTemplateName, sub, viewBag, null);
                }
                catch (Exception ex)
                {
                    MailDemonLog.Error(ex);
                    continue;
                }
                message.From.Clear();
                message.To.Clear();
                if (string.IsNullOrWhiteSpace(list.FromEmailName))
                {
                    message.From.Add(MailboxAddress.Parse(list.FromEmailAddress));
                }
                else
                {
                    message.From.Add(new MailboxAddress(list.FromEmailName, list.FromEmailAddress));
                }
                message.To.Add(MailboxAddress.Parse(sub.EmailAddress));
                yield return new MailToSend { Subscription = sub, Message = message, Callback = callback };
            }
        }

        public BulkMailSender(IMailDemonDatabaseProvider dbProvider)
        {
            this.dbProvider = dbProvider;
        }

        public async Task SendBulkMail(MailList list, IMailCreator mailCreator, IMailSender mailSender, ExpandoObject viewBag,
            bool all, string fullTemplateName, string unsubscribeUrl)
        {
            MailDemonLog.Warn("Started bulk send for {0}", fullTemplateName);

            DateTime now = DateTime.UtcNow;
            int successCount = 0;
            int failCount = 0;
            List<Task> pendingTasks = new List<Task>();
            Stopwatch timer = Stopwatch.StartNew();

            using (var db = dbProvider.GetDatabase())
            {
                void callbackHandler(MailListSubscription _sub, string error)
                {
                    lock (db)
                    {
                        // although this is slow, it is required as we do not want to double email people in the event
                        // that server reboots, loses power, etc. for every message we have to mark that person
                        // with the correct status immediately
                        _sub.Result = error;
                        _sub.ResultTimestamp = DateTime.UtcNow;
                        db.Update(_sub);
                        db.SaveChanges();
                        if (string.IsNullOrWhiteSpace(error))
                        {
                            successCount++;
                        }
                        else
                        {
                            failCount++;
                        }
                    }
                }

                // use a separate database instance to do the query, that way we can update records in our other database instance
                // preventing locking errors, especially with sqlite drivers
                MailDemonLog.Warn("Begin bulk send");
                using (var dbBulk = dbProvider.GetDatabase())
                {
                    IEnumerable<KeyValuePair<string, IEnumerable<MailListSubscription>>> pendingSubs = dbBulk.GetBulkEmailSubscriptions(list, unsubscribeUrl, all);
                    foreach (KeyValuePair<string, IEnumerable<MailListSubscription>> sub in pendingSubs)
                    {
                        now = DateTime.UtcNow;
                        try
                        {
                            IAsyncEnumerable<MailToSend> messagesToSend = GetMessages(sub.Value, mailCreator, list, viewBag, fullTemplateName, callbackHandler);
                            Task task = mailSender.SendMailAsync(sub.Key, messagesToSend);
                            pendingTasks.Add(task);
                        }
                        catch (Exception ex)
                        {
                            MailDemonLog.Error(ex);
                        }
                    }
                }

                await Task.WhenAll(pendingTasks);

                MailDemonLog.Warn("Finished bulk send for {0}, {1} messages succeeded, {2} messages failed in {3:0.00} seconds.", fullTemplateName, successCount, failCount, timer.Elapsed.TotalSeconds);
            }

            GC.Collect();
        }
    }
}
