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
			[Blob("%BlobContainerName%/%BlobFileName%", FileAccess.Read)] Stream stateIn,   
			[Blob("%BlobContainerName%/%BlobFileName%", FileAccess.Write)] TextWriter stateOut, 
			[SendGrid(ApiKey = "%AzureWebJobsSendGridApiKey%")] SendGridMessage message, 
			TraceWriter log, 
			CancellationToken cancellationToken = default(CancellationToken)
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
				Tos = new List<EmailAddress> 
				{
					new EmailAddress(Environment.GetEnvironmentVariable("PrimaryEmail", EnvironmentVariableTarget.Process))
				}
			};
			var secondaryEmail = Environment.GetEnvironmentVariable("SecondaryEmail", EnvironmentVariableTarget.Process);
			if (!string.IsNullOrEmpty(secondaryEmail))
				p.Tos.Add(new EmailAddress(secondaryEmail));

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

			log.Info($"Going to query for '{twitterQuery}' starting at SinceId {previousTweetSinceId}");

			var twitterCtx = new TwitterContext(twitterAuth);
			var searchResponse = (from search in twitterCtx.Search
							  where search.Type == SearchType.Search &&
									search.Query == twitterQuery && 
									search.SinceID == previousTweetSinceId
							  select search).SingleOrDefault();

			// manually assemble
			var sendGridClient = new SendGridClient(Environment.GetEnvironmentVariable("AzureWebJobsSendGridApiKey", EnvironmentVariableTarget.Process));

			message = new SendGridMessage();
			message.Subject = Environment.GetEnvironmentVariable("EmailSubject", EnvironmentVariableTarget.Process);
			message.From = new EmailAddress(Environment.GetEnvironmentVariable("FromEmail", EnvironmentVariableTarget.Process));
			message.Personalizations = new List<Personalization> { p };
			

			var msgText = "";
			var updatedSinceId = previousTweetSinceId;
			var newTweetsCount = 0;
			if (searchResponse != null && searchResponse.Statuses != null)
			{
				var newResponses = searchResponse.Statuses.Where(a => a.StatusID > previousTweetSinceId);
			
				newTweetsCount = newResponses.Count();
				log.Info($"Query has returned {newTweetsCount} new results.");

				foreach (var tweet in newResponses)
				{
					var txt = tweet.Text;
					var tweetId = tweet.StatusID;
					var createdAt = tweet.CreatedAt;

					var urls = "";
					if (tweet.Entities != null && tweet.Entities.UrlEntities != null)
					{
						foreach (var urlEntity in tweet.Entities.UrlEntities)
						{
							urls += urlEntity.ExpandedUrl + " ";
						}
					}

					if (tweetId > updatedSinceId)
						updatedSinceId = tweetId;

					// TODO: format the email body nicer than this!
					msgText += $"{createdAt.ToLocalTime().ToLongDateString()}  {createdAt.ToLocalTime().ToLongTimeString()}\n{txt}\n{urls}\n\n";

				}
			}
			else
				log.Info("Query has returned no results.");
			
			
			// write state out and send notifications (latest sinceid)
			if (newTweetsCount > 0)
			{
				log.Info($"About to attempt to send message: {message}");
			
				message.AddContent("text/plain", msgText);
				var resp = await sendGridClient.SendEmailAsync(message, cancellationToken);

				if (resp.StatusCode != HttpStatusCode.Accepted)
					log.Error($"Sendgrid send email failed with status code {(int)resp.StatusCode}");
			}
			
			var updatedState = updatedSinceId.ToString();
			await stateOut.WriteLineAsync( updatedState);
                
		}
	}
}
