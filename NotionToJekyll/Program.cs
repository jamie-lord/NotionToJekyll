using Microsoft.Extensions.Configuration;
using Notion.Client;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NotionToJekyll
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            IConfigurationRoot config = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json")
                .AddUserSecrets<Program>()
                .Build();

            var clientOptions = new ClientOptions
            {
                AuthToken = config["NotionIntegrationToken"]
            };

            var client = new NotionClient(clientOptions);
            var blocksClient = new BlocksClient(new RestClient(clientOptions));

            var databaseList = await client.Databases.ListAsync();

            var postsDatabase = databaseList.Results.Single(x => x.Title.First().PlainText == "Posts");

            var posts = await client.Databases.QueryAsync(postsDatabase.Id, new DatabasesQueryParameters());

            foreach (Page post in posts.Results)
            {
                var blocks = await blocksClient.RetrieveChildrenAsync(post.Id);

                // Construct frontmatter
                var postFile = "---\n";
                postFile += $"title: {((TitlePropertyValue)post.Properties["Title"]).Title.First().PlainText}\n";
                postFile += $"notion-id: {post.Id}\n";
                postFile += $"date: {((LastEditedTimePropertyValue)post.Properties["Updated"]).LastEditedTime}\n";
                postFile += "---\n\n";
            }
        }
    }
}
