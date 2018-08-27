using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using LinqToTwitter;
using System.Linq;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using System.IO;
using System.Text;
using System;
using System.Collections.Generic;
using System.Threading;
using SendGrid.Helpers.Mail;
using SendGrid;

namespace TwitterMonitor
{
	public static class TwitterMonitor
	{
		[FunctionName("TwitterMonitor")]
		// For testing every minute, can use "0 */1 * * * *"
		// Normally every 5 minutes
		public static async Task Run([TimerTrigger("0 */5 * * * *")]TimerInfo myTimer, 
			[Blob("twitterjob1/twittercheckpoint", FileAccess.Read)] Stream stateIn, 
			[Blob("twitterjob1/twittercheckpoint", FileAccess.Write)] Stream stateOut,  
			TraceWriter log, 
			CancellationToken cancellationToken = default(CancellationToken) 
			//, [SendGrid] out Mail message <-- this doesn't seem to work properly with current nuget version, so do it manually!!!
			)
		{
			if (myTimer == null) throw new Exception("Invalid timer trigger!");
		
			log.Info("Twitter Monitor function starting.");

			var twitterAuth = new SingleUserAuthorizer
			{
				CredentialStore = new SingleUserInMemoryCredentialStore
		        {
		            ConsumerKey = Environment.GetEnvironmentVariable("TwitterApiConsumerKey", EnvironmentVariableTarget.Process), 
		            ConsumerSecret = Environment.GetEnvironmentVariable("TwitterApiConsumerSecret", EnvironmentVariableTarget.Process), 
		            AccessToken = Environment.GetEnvironmentVariable("TwitterApiAccessToken", EnvironmentVariableTarget.Process), 
		            AccessTokenSecret = Environment.GetEnvironmentVariable("TwitterApiTokenSecret", EnvironmentVariableTarget.Process)
		        }
			};

			var p = new Personalization
			{
				Tos = new List<EmailAddress> { 
				new EmailAddress(Environment.GetEnvironmentVariable("PrimaryEmail", EnvironmentVariableTarget.Process)), 
				new EmailAddress(Environment.GetEnvironmentVariable("SecondaryEmail", EnvironmentVariableTarget.Process)) }
			};

			// april 4 2018 (so that we're not asking for beginning of all time)
			var previousTweetSinceId = 981588894067118080UL; 
			
			if (stateIn != null)
			{
				using (var reader = new StreamReader(stateIn, Encoding.UTF8))
				{
					if (!ulong.TryParse(await reader.ReadToEndAsync(), out previousTweetSinceId))
						throw new Exception("must parse");
				}
			}
			var twitterQuery = Environment.GetEnvironmentVariable("TwitterQuery", EnvironmentVariableTarget.Process);

			var twitterCtx = new TwitterContext(twitterAuth);
			var searchResponse = (from search in twitterCtx.Search
							  where search.Type == SearchType.Search &&
									search.Query == twitterQuery && 
									search.SinceID == previousTweetSinceId
							  select search).SingleOrDefault();

			// manually assemble
			var sendGridClient = new SendGridClient(Environment.GetEnvironmentVariable("AzureWebJobsSendGridApiKey", EnvironmentVariableTarget.Process));

			var message = new SendGridMessage
			{
				Subject = Environment.GetEnvironmentVariable("EmailSubject", EnvironmentVariableTarget.Process),
				From = new EmailAddress(Environment.GetEnvironmentVariable("FromEmail", EnvironmentVariableTarget.Process)),
				Personalizations = new List<Personalization> { p }
			};

			var msgText = "";
			var updatedSinceId = 0UL;
			if (searchResponse != null && searchResponse.Statuses != null)
			{
				searchResponse.Statuses.ForEach(tweet =>
				{
					var txt = tweet.Text;
					var tweetId = tweet.StatusID;
					
					if (tweetId > previousTweetSinceId)
						updatedSinceId = tweetId;
						
					// TODO: format the email body nicer than this!
					msgText += Environment.NewLine + txt;
				 	
				});
			}
			
			// write state out and send notifications (latest sinceid)
			if (stateOut != null && updatedSinceId > 0UL)
			{
				message.AddContent("text/plain", msgText);
				var resp = await sendGridClient.SendEmailAsync(message, cancellationToken);
				
				if (resp.StatusCode != HttpStatusCode.Accepted)
				{
					// TODO: error message
				}
				
				using (var writer = new StreamWriter(stateOut, Encoding.UTF8))
				{
					await writer.WriteLineAsync(updatedSinceId.ToString());
				}
			}
			else
			{
				// TODO: error message
			}
                
		}
	}
}
