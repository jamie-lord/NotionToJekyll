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

            // Get full content of all existing post files
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
            var databaseList = await notionClient.Databases.ListAsync();
            var postsDatabase = databaseList.Results.Single(x => x.Title.First().PlainText == "Posts");
            var posts = await notionClient.Databases.QueryAsync(postsDatabase.Id, new DatabasesQueryParameters());
            var postsToUnpublish = new Dictionary<string, (RepositoryContent, PostFrontMatter)>();

            // Process each post in Notion
            foreach (Notion.Client.Page post in posts.Results)
            {
                var published = ((CheckboxPropertyValue)post.Properties["Published"]).Checkbox;
                var postFileName = ((RichTextPropertyValue)post.Properties["File name"]).RichText.First().PlainText;

                string[] categories = ((MultiSelectPropertyValue)post.Properties["Categories"]).MultiSelect.Select(x => x.Name).ToArray();
                string[] tags = ((MultiSelectPropertyValue)post.Properties["Tags"]).MultiSelect.Select(x => x.Name).ToArray();

                var postFrontMatter = new PostFrontMatter
                {
                    Title = ((TitlePropertyValue)post.Properties["Title"]).Title.First().PlainText,
                    Date = DateTime.Parse(((CreatedTimePropertyValue)post.Properties["Created"]).CreatedTime),
                    Modified = DateTime.Parse(((LastEditedTimePropertyValue)post.Properties["Modified"]).LastEditedTime),
                    NotionId = post.Id,
                    Categories = categories,
                    Tags = tags
                };

                // Construct frontmatter
                var postFileContent = "---\n";
                postFileContent += yamlSerializer.Serialize(postFrontMatter);
                postFileContent += "---\n\n";

                // Get all Notion blocks for this post
                PaginatedList<Block> blocks = await notionClient.Blocks.RetrieveChildrenAsync(post.Id);
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
                            postFileContent += pb.Paragraph.Text.RichTextToMarkdown();
                            break;
                        case BlockType.Heading_1:
                            postFileContent += $"# {((HeadingOneBlock)block).Heading_1.Text.RichTextToMarkdown()}";
                            break;
                        case BlockType.Heading_2:
                            postFileContent += $"## {((HeadingTwoBlock)block).Heading_2.Text.RichTextToMarkdown()}";
                            break;
                        case BlockType.Heading_3:
                            postFileContent += $"### {((HeadingThreeeBlock)block).Heading_3.Text.RichTextToMarkdown()}";
                            break;
                        case BlockType.BulletedListItem:
                            postFileContent += $"- {((BulletedListItemBlock)block).BulletedListItem.Text.RichTextToMarkdown()}";
                            break;
                        case BlockType.NumberedListItem:
                            postFileContent += $"{numberedListItemNumber}. {((NumberedListItemBlock)block).NumberedListItem.Text.RichTextToMarkdown()}";
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
                                postFileContent += $"- [x] {todo.Text.RichTextToMarkdown()}";
                            }
                            else
                            {
                                postFileContent += $"- [ ] {todo.Text.RichTextToMarkdown()}";
                            }
                            break;
                        case BlockType.Toggle:
                        case BlockType.ChildPage:
                        case BlockType.Unsupported:
                            postFileContent += $"**Unsupported Notion blocktype '{block.Type}'**\n{{: .notice--danger}}";
                            break;
                        default:
                            break;
                    }

                    if (i != blocks.Results.Count - 1 &&
                        (block.Type == BlockType.BulletedListItem || block.Type == BlockType.NumberedListItem || block.Type == BlockType.ToDo) &&
                        blocks.Results[i + 1].Type == block.Type)
                    {
                        postFileContent += "\n";
                    }
                    else
                    {
                        postFileContent += "\n\n";
                    }
                }

                // Update existing post with matching Notion ID and newer date
                if (existingPosts.ContainsKey(post.Id) && existingPosts[post.Id].Item2.Modified < postFrontMatter.Modified && published)
                {
                    await gitHubClient.Repository.Content.UpdateFile(repoOwner, repoName, existingPosts[post.Id].Item1.Path, new UpdateFileRequest($"Updated post '{postFrontMatter.Title}'", postFileContent, existingPosts[post.Id].Item1.Sha));
                }
                // Create new post
                else if (!existingPosts.ContainsKey(post.Id) && published)
                {
                    await gitHubClient.Repository.Content.CreateFile(repoOwner, repoName, $"{postsDirectory}/{postFileName}.markdown", new CreateFileRequest($"Added post '{postFrontMatter.Title}'", postFileContent));
                }
                else if (existingPosts.ContainsKey(post.Id) && !published)
                {
                    postsToUnpublish.Add(post.Id, existingPosts[post.Id]);
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

            // Delete posts that exist but are no longer published
            foreach (KeyValuePair<string, (RepositoryContent, PostFrontMatter)> item in postsToUnpublish)
            {
                await gitHubClient.Repository.Content.DeleteFile(repoOwner, repoName, item.Value.Item1.Path, new DeleteFileRequest($"Deleted post '{item.Value.Item2.Title}'", item.Value.Item1.Sha));
            }
        }

        public class PostFrontMatter
        {
            [YamlMember(Alias = "title")]
            public string Title { get; set; }

            [YamlMember(Alias = "date")]
            public DateTime Date { get; set; }

            [YamlMember(Alias = "last_modified_at")]
            public DateTime Modified { get; set; }

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
