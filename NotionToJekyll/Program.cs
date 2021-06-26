using Microsoft.Extensions.Configuration;
using Notion.Client;
using Octokit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

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


            var gitHubClient = new GitHubClient(new ProductHeaderValue("notion-to-jekyll"));
            var tokenAuth = new Credentials(config["GitHubPat"]);
            gitHubClient.Credentials = tokenAuth;

            var clientOptions = new ClientOptions
            {
                AuthToken = config["NotionIntegrationToken"]
            };

            var repoOwner = config["GitHubRepoOwner"];
            var repoName = config["GitHubRepoName"];
            var postsDirectory = config["GitHubPostsDirectory"];

            var yamlSerializer = new SerializerBuilder().Build();
            var yamlDeserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

            // Get all existing posts from GitHub
            IReadOnlyList<RepositoryContent> existingPostFiles = await gitHubClient.Repository.Content.GetAllContents(repoOwner, repoName, postsDirectory);
            var existingPosts = new Dictionary<string, (RepositoryContent, PostFrontMatter)>();
            foreach (var existingPostFile in existingPostFiles)
            {
                RepositoryContent post = (await gitHubClient.Repository.Content.GetAllContents(repoOwner, repoName, existingPostFile.Path)).Single();

                // Deserialise Front Matter and get Notion ID and date
                int pFrom = post.Content.IndexOf("---\n") + "---\n".Length;
                int pTo = post.Content.LastIndexOf("---\n\n");

                if (pFrom == -1 || pTo == -1)
                {
                    // File doesn't appear to have Front Matter
                    continue;
                }

                var frontMatterString = post.Content.Substring(pFrom, pTo - pFrom);
                PostFrontMatter fm = yamlDeserializer.Deserialize<PostFrontMatter>(frontMatterString);
                if (fm.NotionId != null)
                {
                    existingPosts.Add(fm.NotionId, (post, fm));
                }
            }

            var notionClient = new NotionClient(clientOptions);
            var notionBlocksClient = new BlocksClient(new RestClient(clientOptions));

            var databaseList = await notionClient.Databases.ListAsync();
            var postsDatabase = databaseList.Results.Single(x => x.Title.First().PlainText == "Posts");
            var posts = await notionClient.Databases.QueryAsync(postsDatabase.Id, new DatabasesQueryParameters());

            foreach (Notion.Client.Page post in posts.Results)
            {
                var postFrontMatter = new PostFrontMatter
                {
                    Title = ((TitlePropertyValue)post.Properties["Title"]).Title.First().PlainText,
                    Date = DateTime.Parse(((LastEditedTimePropertyValue)post.Properties["Updated"]).LastEditedTime),
                    NotionId = post.Id
                };

                var yaml = yamlSerializer.Serialize(postFrontMatter);

                // Construct frontmatter
                var postFile = "---\n";
                postFile += yaml;
                postFile += "---\n\n";

                PaginatedList<Block> blocks = await notionBlocksClient.RetrieveChildrenAsync(post.Id);
                int numberedListItemNumber = 1;
                for (int i = 0; i < blocks.Results.Count; i++)
                {
                    var block = blocks.Results[i];

                    switch (block.Type)
                    {
                        case BlockType.Paragraph:
                            var pb = (ParagraphBlock)block;
                            if (!pb.Paragraph.Text.Any())
                            {
                                // Empty paragraph block, skip it
                                continue;
                            }
                            postFile += pb.Paragraph.Text.RichTextToMarkdown();
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
                            postFile += $"- {((BulletedListItemBlock)block).BulletedListItem.Text.RichTextToMarkdown()}";
                            break;
                        case BlockType.NumberedListItem:
                            postFile += $"{numberedListItemNumber}. {((NumberedListItemBlock)block).NumberedListItem.Text.RichTextToMarkdown()}";
                            if (i != blocks.Results.Count && blocks.Results[i + 1].Type != BlockType.NumberedListItem)
                            {
                                numberedListItemNumber = 1;
                            }
                            else
                            {
                                numberedListItemNumber++;
                            }
                            break;
                        case BlockType.ToDo:
                            var todo = ((ToDoBlock)block).ToDo;
                            if (todo.IsChecked)
                            {
                                postFile += $"- [x] {todo.Text.RichTextToMarkdown()}";
                            }
                            else
                            {
                                postFile += $"- [ ] {todo.Text.RichTextToMarkdown()}";
                            }
                            break;
                        case BlockType.Toggle:
                        case BlockType.ChildPage:
                        case BlockType.Unsupported:
                            postFile += $"**Unsupported blocktype '{block.Type}'**\n{{: .notice--danger}}";
                            break;
                        default:
                            break;
                    }

                    if (i != blocks.Results.Count - 1 &&
                        (block.Type == BlockType.BulletedListItem || block.Type == BlockType.NumberedListItem || block.Type == BlockType.ToDo) &&
                        blocks.Results[i + 1].Type == block.Type)
                    {
                        postFile += "\n";
                    }
                    else
                    {
                        postFile += "\n\n";
                    }
                }

                var permalink = ((RichTextPropertyValue)post.Properties["Permalink"]).RichText.First().PlainText;

                // Update existing post with matching Notion ID and newer date
                if (existingPosts.ContainsKey(post.Id) && existingPosts[post.Id].Item2.Date < postFrontMatter.Date)
                {
                    await gitHubClient.Repository.Content.UpdateFile(repoOwner, repoName, existingPosts[post.Id].Item1.Path, new UpdateFileRequest($"Updated post '{postFrontMatter.Title}'", postFile, existingPosts[post.Id].Item1.Sha));
                }
                // Create new post
                else if (!existingPosts.ContainsKey(post.Id))
                {
                    await gitHubClient.Repository.Content.CreateFile(repoOwner, repoName, $"{postsDirectory}/{permalink}.markdown", new CreateFileRequest($"Added post '{postFrontMatter.Title}'", postFile));
                }
            }

            // Delete posts that no longer exist in Notion
            foreach (KeyValuePair<string, (RepositoryContent, PostFrontMatter)> item in existingPosts)
            {
                if (!posts.Results.Any(x => x.Id == item.Key))
                {
                    await gitHubClient.Repository.Content.DeleteFile(repoOwner, repoName, item.Value.Item1.Path, new DeleteFileRequest($"Deleted post '{item.Value.Item2.Title}'", item.Value.Item1.Sha));
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
            string text = richText.PlainText;

            if (richText.Href != null)
            {
                text = $"[{text}]({richText.Href})";
            }

            if (richText.Annotations.IsBold && richText.Annotations.IsItalic)
            {
                return $"***{text}***";
            }

            if (richText.Annotations.IsBold)
            {
                return $"**{text}**";
            }

            if (richText.Annotations.IsItalic)
            {
                return $"*{text}*";
            }

            if (richText.Annotations.IsCode)
            {
                return $"`{text}`";
            }

            if (richText.Annotations.IsStrikeThrough)
            {
                return $"~~{text}~~";
            }

            if (richText.Annotations.IsUnderline)
            {
                return $"<u>{text}</u>";
            }

            return text;
        }
    }
}
