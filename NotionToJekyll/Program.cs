using Microsoft.Extensions.Configuration;
using Notion.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
                var postFrontMatter = new PostFrontMatter
                {
                    Title = ((TitlePropertyValue)post.Properties["Title"]).Title.First().PlainText,
                    Date = DateTime.Parse(((LastEditedTimePropertyValue)post.Properties["Updated"]).LastEditedTime),
                    NotionId = post.Id
                };

                var serializer = new SerializerBuilder()
                    .WithNamingConvention(PascalCaseNamingConvention.Instance)
                    .Build();
                var yaml = serializer.Serialize(postFrontMatter);

                // Construct frontmatter
                var postFile = "---\n";
                postFile += yaml;
                postFile += "---\n\n";

                PaginatedList<Block> blocks = await blocksClient.RetrieveChildrenAsync(post.Id);
                foreach (Block block in blocks.Results)
                {
                    switch (block.Type)
                    {
                        case BlockType.Paragraph:
                            postFile += ((ParagraphBlock)block).Paragraph.Text.RichTextToMarkdown();
                            break;
                        case BlockType.Heading_1:
                            postFile += $"# {((HeadingOneBlock)block).Heading_1.Text.RichTextToMarkdown()}";
                            break;
                        case BlockType.Heading_2:
                            postFile += $"## {((HeadingTwoBlock)block).Heading_2.Text.RichTextToMarkdown()}";
                            break;
                        case BlockType.Heading_3:
                            postFile += $"### {((HeadingThreeeBlock)block).Heading_3.Text.RichTextToMarkdown()}";
                            break;
                        case BlockType.BulletedListItem:
                            break;
                        case BlockType.NumberedListItem:
                            break;
                        case BlockType.ToDo:
                            break;
                        case BlockType.Toggle:
                            break;
                        case BlockType.ChildPage:
                            break;
                        case BlockType.Unsupported:
                            break;
                        default:
                            break;
                    }
                    postFile += "\n\n";
                }
            }
        }

        public class PostFrontMatter
        {
            [YamlMember(Alias = "title")]
            public string Title { get; set; }

            [YamlMember(Alias = "date")]
            public DateTime Date { get; set; }

            [YamlMember(Alias = "notion_id")]
            public string NotionId { get; set; }

            [YamlMember(Alias = "categories")]
            public string[] Categories { get; set; }

            [YamlMember(Alias = "tags")]
            public string[] Tags { get; set; }
        }
    }

    public static class MarkdownHelper
    {
        public static string RichTextToMarkdown(this IEnumerable<RichTextBase> text)
        {
            string output = "";

            foreach (RichTextBase t in text)
            {
                switch (t.Type)
                {
                    case RichTextType.Text:
                        output += ((RichTextText)t).RichTextToMarkdown();
                        break;
                    case RichTextType.Unknown:
                    case RichTextType.Mention:
                    case RichTextType.Equation:
                        throw new Exception($"Unsupported RichTextBase of type {t.Type}");
                }
            }

            return output;
        }

        private static string RichTextToMarkdown(this IEnumerable<RichTextText> text)
        {
            string output = "";

            foreach (RichTextText richText in text)
            {
                output += richText.RichTextToMarkdown();
            }

            return output;
        }

        private static string RichTextToMarkdown(this RichTextText richText)
        {
            if (richText.Annotations.IsBold && richText.Annotations.IsItalic)
            {
                return $"***{richText.PlainText}***";
            }

            if (richText.Annotations.IsBold)
            {
                return $"**{richText.PlainText}**";
            }

            if (richText.Annotations.IsItalic)
            {
                return $"*{richText.PlainText}*";
            }

            if (richText.Annotations.IsCode)
            {
                return $"`{richText.PlainText}`";
            }

            if (richText.Annotations.IsStrikeThrough)
            {
                return $"~~{richText.PlainText}~~";
            }

            if (richText.Annotations.IsUnderline)
            {
                return $"<u>{richText.PlainText}<u/>";
            }

            return richText.PlainText;
        }
    }
}
