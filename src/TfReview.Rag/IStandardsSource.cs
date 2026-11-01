namespace TfReview.Rag;

public interface IStandardsSource
{
    /// <summary>Puts the corpus a config names into a directory, or throws <see cref="StandardsFetchException"/>.</summary>
    Task FetchAsync(TfReviewConfig config, string targetDirectory, CancellationToken cancellationToken = default);
}
