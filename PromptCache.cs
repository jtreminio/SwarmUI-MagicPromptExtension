using SwarmUI.Utils;

namespace Hartsy.Extensions.MagicPromptExtension;

public class PromptCache
{
    private readonly Dictionary<string, string> _cache = new();
    private readonly LinkedList<string> _accessOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _cacheNodes = new();
    private readonly Dictionary<string, TaskCompletionSource<PendingResult>> _pendingRequests = new();
    private readonly Dictionary<string, int> _attemptCounts = new();
    private readonly Dictionary<string, int> _activeCallers = new();
    private readonly object _lock = new();

    private readonly int _maxSize;
    private const int DefaultTimeoutMs = 90_000;

    /// <summary>Maximum number of LLM calls made for one cache key while callers are actively contending for it.
    /// The count resets once every caller for that key has finished, so a later batch starts fresh.</summary>
    private const int MaxAttempts = 2;

    /// <summary>Outcome handed from the thread that owned an attempt to the threads waiting on it.
    /// A null <see cref="Value"/> means the attempt failed and <see cref="Error"/> explains why.</summary>
    private sealed record PendingResult(string Value, string Error);

    public PromptCache(int maxSize = 1000)
    {
        _maxSize = maxSize;
    }

    /// <summary>
    /// Gets a cached result or creates a new one using the provided function.
    /// Handles request deduplication - if another thread is already fetching the same key,
    /// this thread will wait for that result instead of making a duplicate request.
    /// If the in-flight request fails, the waiting threads do not all fail with it: one of them
    /// takes over and retries (still only one request in flight at a time), up to <see cref="MaxAttempts"/>
    /// calls total for the key. Returns null once the attempts are exhausted, with <paramref name="error"/>
    /// describing the last failure.
    /// </summary>
    public string GetOrCreate(string prompt, string instructionId, string modelId, string thinking, Func<string> createValue, int timeoutMs, out string error)
    {
        var cacheKey = BuildCacheKey(prompt, instructionId, modelId, thinking);
        var effectiveTimeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
        error = null;

        lock (_lock)
        {
            if (TryGetFromCacheLocked(cacheKey, out var cachedResult))
            {
                return cachedResult;
            }

            _activeCallers[cacheKey] = _activeCallers.GetValueOrDefault(cacheKey) + 1;
        }

        try
        {
            while (true)
            {
                TaskCompletionSource<PendingResult> pendingTcs = null;

                lock (_lock)
                {
                    if (TryGetFromCacheLocked(cacheKey, out var cachedResult))
                    {
                        return cachedResult;
                    }

                    if (_pendingRequests.TryGetValue(cacheKey, out var existingTcs))
                    {
                        Logs.Debug("MagicPromptExtension.PromptCache: another thread is already fetching this prompt, waiting...");
                        pendingTcs = existingTcs;
                    }
                    else if (_attemptCounts.GetValueOrDefault(cacheKey) >= MaxAttempts)
                    {
                        Logs.Debug($"MagicPromptExtension.PromptCache: giving up, {MaxAttempts} attempts already failed for this prompt");
                        error ??= $"LLM request failed {MaxAttempts} times";
                        return null;
                    }
                    else
                    {
                        // We are the owner of this attempt - create a TaskCompletionSource for other threads to wait on
                        _attemptCounts[cacheKey] = _attemptCounts.GetValueOrDefault(cacheKey) + 1;
                        _pendingRequests[cacheKey] = new TaskCompletionSource<PendingResult>();
                    }
                }

                // If another thread was already fetching, wait for it OUTSIDE the lock
                if (pendingTcs != null)
                {
                    var waited = WaitForPendingRequest(pendingTcs, effectiveTimeout, out bool retryable, out string waitError);
                    if (waited != null)
                    {
                        return waited;
                    }

                    error = waitError ?? error;
                    if (!retryable)
                    {
                        return null;
                    }

                    // The owner failed - loop back to either retry ourselves or wait on whoever got there first
                    continue;
                }

                // Make the request OUTSIDE the lock to avoid blocking other threads
                string result = null;
                string attemptError = null;
                try
                {
                    result = createValue();
                    if (string.IsNullOrEmpty(result))
                    {
                        result = null;
                        attemptError = "LLM returned an empty response";
                    }
                }
                catch (Exception ex)
                {
                    attemptError = ex.Message;
                    Logs.Error($"MagicPromptExtension.PromptCache: factory failed: {ex.Message}");
                }

                lock (_lock)
                {
                    if (result != null)
                    {
                        AddToCacheLocked(cacheKey, result);
                        _attemptCounts.Remove(cacheKey);
                    }

                    SignalPendingRequestLocked(cacheKey, result, attemptError);
                    CleanupPendingRequestLocked(cacheKey);
                }

                if (result != null)
                {
                    return result;
                }

                // Our attempt failed - loop back, another attempt may still be available
                error = attemptError;
            }
        }
        finally
        {
            lock (_lock)
            {
                int remaining = _activeCallers.GetValueOrDefault(cacheKey) - 1;
                if (remaining > 0)
                {
                    _activeCallers[cacheKey] = remaining;
                }
                else
                {
                    // Last caller for this key is done - forget the failed attempts so a later batch starts fresh
                    _activeCallers.Remove(cacheKey);
                    _attemptCounts.Remove(cacheKey);
                }
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _accessOrder.Clear();
            _cacheNodes.Clear();
            _attemptCounts.Clear();
            _activeCallers.Clear();

            foreach (var tcs in _pendingRequests.Values)
            {
                tcs.TrySetCanceled();
            }
            _pendingRequests.Clear();
        }
    }

    private static string BuildCacheKey(string prompt, string instructionId, string modelId, string thinking)
    {
        var key = NormalizePrompt(prompt);
        if (!string.IsNullOrEmpty(instructionId))
        {
            key += $"||{instructionId.ToLowerInvariant()}";
        }
        if (!string.IsNullOrEmpty(modelId))
        {
            key += $"##{modelId.ToLowerInvariant()}";
        }
        if (!string.IsNullOrEmpty(thinking) && !thinking.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            key += $"@@{thinking.ToLowerInvariant()}";
        }
        return key;
    }

    private static string NormalizePrompt(string prompt)
    {
        return string.IsNullOrWhiteSpace(prompt)
            ? string.Empty
            : new string(prompt.Trim().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    private bool TryGetFromCacheLocked(string key, out string cachedResult)
    {
        if (!_cache.TryGetValue(key, out cachedResult))
        {
            return false;
        }

        Logs.Debug("MagicPromptExtension.PromptCache: cache hit");
        if (_cacheNodes.TryGetValue(key, out var node))
        {
            _accessOrder.Remove(node);
            _accessOrder.AddLast(node);
        }
        return true;
    }

    private void AddToCacheLocked(string key, string value)
    {
        while (_cache.Count >= _maxSize)
        {
            var oldestNode = _accessOrder.First;
            if (oldestNode != null)
            {
                var oldestKey = oldestNode.Value;
                _cache.Remove(oldestKey);
                _cacheNodes.Remove(oldestKey);
                _accessOrder.RemoveFirst();
            }
            else
            {
                break;
            }
        }

        _cache[key] = value;

        if (_cacheNodes.TryGetValue(key, out var existingNode))
        {
            _accessOrder.Remove(existingNode);
        }

        var newNode = _accessOrder.AddLast(key);
        _cacheNodes[key] = newNode;
    }

    private void SignalPendingRequestLocked(string key, string result, string error)
    {
        if (_pendingRequests.TryGetValue(key, out var tcs))
        {
            tcs.TrySetResult(new PendingResult(result, error));
        }
    }

    private void CleanupPendingRequestLocked(string key)
    {
        _pendingRequests.Remove(key);
    }

    /// <summary>Waits on an in-flight attempt owned by another thread. Returns the value on success, or null on
    /// failure with <paramref name="retryable"/> set when it is worth looping back for another attempt.</summary>
    private static string WaitForPendingRequest(TaskCompletionSource<PendingResult> tcs, int timeoutMs, out bool retryable, out string error)
    {
        retryable = false;
        error = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (!tcs.Task.Wait(timeoutMs))
            {
                Logs.Warning($"MagicPromptExtension.PromptCache: timeout after {timeoutMs}ms waiting for pending request");
                error = $"Timed out after {timeoutMs}ms waiting on an in-flight LLM request";
                return null;
            }

            stopwatch.Stop();
            var result = tcs.Task.Result;

            if (result?.Value == null)
            {
                Logs.Debug("MagicPromptExtension.PromptCache: waited for owner request, but it failed - retrying if attempts remain");
                error = result?.Error;
                retryable = true;
                return null;
            }

            Logs.Debug($"MagicPromptExtension.PromptCache: waited {stopwatch.ElapsedMilliseconds}ms for owner request");
            return result.Value;
        }
        catch (OperationCanceledException)
        {
            Logs.Debug("MagicPromptExtension.PromptCache: pending request was cancelled");
            error = "The in-flight LLM request was cancelled";
            return null;
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            Logs.Debug("MagicPromptExtension.PromptCache: pending request was cancelled");
            error = "The in-flight LLM request was cancelled";
            return null;
        }
        catch (Exception ex)
        {
            Logs.Error($"MagicPromptExtension.PromptCache: error waiting for pending request: {ex.Message}");
            error = ex.Message;
            return null;
        }
    }
}
