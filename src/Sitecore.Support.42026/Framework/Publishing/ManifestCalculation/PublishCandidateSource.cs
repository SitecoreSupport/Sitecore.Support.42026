using Sitecore.Framework.Conditions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sitecore.Framework.Publishing;
using Sitecore.Framework.Publishing.Item;
using Sitecore.Framework.Publishing.TemplateGraph;
using Sitecore.Framework.Publishing.Workflow;
using Sitecore.Framework.Publishing.Locators;
using Sitecore.Framework.Publishing.ManifestCalculation;

namespace Sitecore.Support.Framework.Publishing.ManifestCalculation
{
  public class PublishCandidateSource : IPublishCandidateSource
  {
    private static readonly DateTime MaxUtc = DateTime.MaxValue.ToUniversalTime();
    private static readonly DateTime MinUtc = DateTime.MinValue.ToUniversalTime();
    private static readonly IVarianceIdentifierComparer IdentifierComparer = new IVarianceIdentifierComparer();

    private class CacheablePublishable
    {
      public CacheablePublishable(IPublishCandidate node, CacheablePublishable parent)
      {
        Node = node;
        Parent = parent;
      }

      public IPublishCandidate Node { get; }

      public CacheablePublishable Parent { get; }
    }

    private readonly Dictionary<Guid, CacheablePublishable> _ancestorCache = new Dictionary<Guid, CacheablePublishable>();
    private readonly SemaphoreSlim _ancestorLock = new SemaphoreSlim(1);

    private readonly Guid[] _mediaFieldsIds;
    private readonly bool _contentAvailabilityEnabled;

    private readonly string _sourceStore;
    private readonly Dictionary<Guid, IPublishableWorkflowState> _publishableStates;
    private readonly ITemplateGraph _publishingTemplateGraph;
    private readonly ICompositeItemReadRepository _itemReadRepo;
    private readonly IItemRelationshipRepository _itemRelationshipRepo;
    private readonly NodeQueryContext _queryContext;

    public PublishCandidateSource(
        string sourceStore,
        ICompositeItemReadRepository itemReadRepo,
        IItemRelationshipRepository itemRelationshipRepo,
        ITemplateGraph publishingTemplateGraph,
        IWorkflowStateRepository workflowRepo,
        Language[] publishLanguages,
        Guid[] publishFields,
        Guid[] mediaFieldIds,
        bool contentAvailabilityEnabled)
    {
      Condition.Requires(sourceStore, nameof(sourceStore)).IsNotNull();
      Condition.Requires(itemReadRepo, nameof(itemReadRepo)).IsNotNull();
      Condition.Requires(itemRelationshipRepo, nameof(itemRelationshipRepo)).IsNotNull();
      Condition.Requires(publishingTemplateGraph, nameof(publishingTemplateGraph)).IsNotNull();
      Condition.Requires(workflowRepo, nameof(workflowRepo)).IsNotNull();
      Condition.Requires(publishLanguages, nameof(publishLanguages)).IsNotNull();
      Condition.Requires(publishFields, nameof(publishFields)).IsNotNull();
      Condition.Requires(mediaFieldIds, nameof(mediaFieldIds)).IsNotNull();

      _sourceStore = sourceStore;
      _mediaFieldsIds = mediaFieldIds;
      _contentAvailabilityEnabled = contentAvailabilityEnabled;
      _itemReadRepo = itemReadRepo;
      _itemRelationshipRepo = itemRelationshipRepo;
      _publishingTemplateGraph = publishingTemplateGraph;
      _queryContext = new NodeQueryContext(publishLanguages, publishFields);

      // Load the publishCandidate workflow states into memory (there won't be many)
      _publishableStates = workflowRepo.GetPublishableStates(PublishingConstants.WorkflowFields.Final, PublishingConstants.WorkflowFields.PreviewPublishTarget).Result
          .ToDictionary(s => s.StateId, s => s);
    }

    public async Task<IPublishCandidate> GetNode(Guid id)
    {
      var node = await _itemReadRepo.GetItemNode(id, _queryContext).ConfigureAwait(false);

      if (node == null) return null;

      return BuildPublishable(node);
    }

    public async Task<IEnumerable<IPublishCandidate>> GetNodes(IReadOnlyCollection<Guid> ids)
    {
      var nodes = await _itemReadRepo.GetItemNodes(ids, _queryContext).ConfigureAwait(false);

      return nodes.Select(n =>
      {
        if (n == null) return null; // account for deleted items since the publish started.
        return BuildPublishable(n);
      });
    }

    public async Task<IEnumerable<IPublishCandidate>> GetAncestors(IPublishCandidate node)
    {
      // We cannot support concurrent executions due to the nature of the caching logic, so to
      // be thread safe, we lock..
      await _ancestorLock.WaitAsync();
      try
      {
        // is it the root item?
        if (node.ParentId == null) return Enumerable.Empty<IPublishCandidate>();

        var parent = GetFromCache(node.ParentId.Value);

        // special (common) case optimization
        if (parent != null) return YieldAncestorChain(parent);

        // Load and cache the ancestors that we need...
        var ancestors = await _itemReadRepo.GetItemNodeAncestors(node.Id).ConfigureAwait(false);

        // walk up the ancestor chain, collecting ids not in the cache, until we find one that is, or we hit the root
        var uncachedAncestorIds = new List<Guid>();
        CacheablePublishable rootInCache = null;
        foreach (var ancestorId in ancestors)
        {
          rootInCache = GetFromCache(ancestorId);
          if (rootInCache != null) break;
          uncachedAncestorIds.Add(ancestorId);
        }

        // some ancestors were not in the cache, so load them, and set them up in the cache
        if (uncachedAncestorIds.Any())
        {
          uncachedAncestorIds.Reverse();
          var uncachedAncestors = await _itemReadRepo.GetItemNodes(uncachedAncestorIds.ToArray(), _queryContext).ConfigureAwait(false);

          foreach (var uncachedAncestor in uncachedAncestors)
          {
            rootInCache = AddToCache(BuildPublishable(uncachedAncestor), rootInCache);
          }
        }

        return YieldAncestorChain(rootInCache);
      }
      finally
      {
        _ancestorLock.Release();
      }
    }

    public async Task<IEnumerable<IPublishCandidate>> GetChildren(IReadOnlyCollection<Guid> parentIds, int skip, int take)
    {
      var children = await _itemReadRepo.GetChildNodes(
              parentIds,
              _queryContext,
              skip,
              take).ConfigureAwait(false);

      return children.Select(BuildPublishable).ToArray();
    }

    public async Task<IEnumerable<Guid>> GetChildIds(IReadOnlyCollection<Guid> parentIds)
    {
      return await _itemReadRepo.GetChildIds(parentIds);
    }

    public async Task<IEnumerable<IPublishCandidate>> GetRelatedNodes(
        IReadOnlyCollection<IItemVariantIdentifier> locators,
        bool includeRelatedContent,
        bool includeClones)
    {
      var inRelsFilter = new HashSet<ItemRelationshipType>();
      var outRelsFilter = new HashSet<ItemRelationshipType>();

      if (includeRelatedContent)
      {
        outRelsFilter.Add(ItemRelationshipType.CloneOf);
        outRelsFilter.Add(ItemRelationshipType.CloneVersionOf);
        outRelsFilter.Add(ItemRelationshipType.DefaultedBy);
        outRelsFilter.Add(ItemRelationshipType.InheritsFrom);
        outRelsFilter.Add(ItemRelationshipType.TemplatedBy);
        outRelsFilter.Add(ItemRelationshipType.ContentComposedOf);
        if (includeClones)
        {
          inRelsFilter.Add(ItemRelationshipType.CloneOf);
          inRelsFilter.Add(ItemRelationshipType.CloneVersionOf);
        }
      }

      if (!inRelsFilter.Any() && !outRelsFilter.Any())
        return Enumerable.Empty<IPublishCandidate>();

      var itemLocators = locators.ToList();
      Guid[] distinctRelatedIds;

      if (outRelsFilter.Any() && !inRelsFilter.Any())
      {
        var outRels = await _itemRelationshipRepo.GetOutRelationships(
                _sourceStore,
                itemLocators,
                outRelsFilter).ConfigureAwait(false);

        distinctRelatedIds = outRels
            .SelectMany(x => x.Value.Select(r => r.SourceId))
            .Distinct()
            .ToArray();
      }
      else if (inRelsFilter.Any() && !outRelsFilter.Any())
      {
        var inRels = await _itemRelationshipRepo.GetInRelationships(
                _sourceStore,
                itemLocators,
                inRelsFilter).ConfigureAwait(false);

        distinctRelatedIds = inRels
            .SelectMany(x => x.Value.Select(r => r.SourceId))
            .Distinct()
            .ToArray();
      }
      else
      {
        var allRels = await _itemRelationshipRepo.GetAllRelationships(
                _sourceStore,
                itemLocators,
                outRelsFilter,
                inRelsFilter);

        distinctRelatedIds = allRels
            .SelectMany(x => x.Value.Out.Select(r => r.TargetId).Concat(x.Value.In.Select(r => r.SourceId)))
            .Distinct()
            .ToArray();
      }

      var relatedNodes = await _itemReadRepo.GetItemNodes(distinctRelatedIds, _queryContext).ConfigureAwait(false);

      return relatedNodes.Select(BuildPublishable).ToArray();
    }

    private CacheablePublishable GetFromCache(Guid id)
    {
      CacheablePublishable cached;
      if (_ancestorCache.TryGetValue(id, out cached))
        return cached;

      return null;
    }

    private CacheablePublishable AddToCache(IPublishCandidate node, CacheablePublishable parent)
    {
      var cached = new CacheablePublishable(node, parent);
      _ancestorCache.Add(node.Id, cached);
      return cached;
    }

    private IEnumerable<IPublishCandidate> YieldAncestorChain(CacheablePublishable node)
    {
      var ancestorChain = new List<IPublishCandidate>();
      while (node != null)
      {
        ancestorChain.Add(node.Node);
        node = node.Parent;
      }

      ancestorChain.Reverse();
      return ancestorChain;
    }

    private IPublishCandidate BuildPublishable(IItemNode node)
    {
      var standardValuesFields = _publishingTemplateGraph.GetStandardValues(node.Properties.TemplateId).EnsureEnumerated();
      IReadOnlyCollection<IFieldData> fieldsToMerge = new IFieldData[0];
      var isMedia = IsMediaItem(node);
      var isClone = IsClonedItem(node);
      if (isClone)
      {
        var sourceItemId = ParseSitecoreItemUriField(node.InvariantFields, PublishingConstants.Clones.SourceItem);
        var sourceNodeFieldsData = GetCloneSourceNodeFieldsData(node).Result.ToArray();
        if (sourceNodeFieldsData.Any())
        {
          // Only find fields in standards values that don't have values in the cloned item
          var fieldsOnlyInStandardValues =
              standardValuesFields.Where(x => sourceNodeFieldsData.All(s => s.FieldId != x.FieldId));

          // append to the standard values fields
          fieldsToMerge = sourceNodeFieldsData.Concat(fieldsOnlyInStandardValues).ToArray();
        }
      }
      else
      {
        fieldsToMerge = standardValuesFields;
      }

      var finalItem = MergeFieldDataValues(node, fieldsToMerge);

      var sharedRestrictions = ExtractSharedRestrictions(finalItem, _contentAvailabilityEnabled);

      return new PublishCandidate(
          node,
          sharedRestrictions,
          ExtractVariantRestrictions(finalItem, sharedRestrictions),
          isMedia,
          isClone);
    }

    private bool IsClonedItem(IItemNode node)
    {
      return node.InvariantFields.Any(x => x.FieldId == PublishingConstants.Clones.SourceItem && !string.IsNullOrWhiteSpace(x.RawValue));
    }

    private bool IsMediaItem(IItemNode node)
    {
      var mediaFields = node.InvariantFields.Where(x => _mediaFieldsIds.Contains(x.FieldId))
          .Concat(
              node.LanguageVariantFields.SelectMany(x => x.Value).Where(x => _mediaFieldsIds.Contains(x.FieldId)))
          .Concat(
              node.VariantFields.SelectMany(x => x.Value).Where(x => _mediaFieldsIds.Contains(x.FieldId)));

      Guid mediaId;
      return mediaFields.Any(f => Guid.TryParse(f.RawValue, out mediaId));

    }

    private async Task<IEnumerable<IFieldData>> GetCloneSourceNodeFieldsData(IItemNode node)
    {
      IItemNode sourceNode = null;
      var sourcesFieldData = new List<IFieldData>();
      do
      {
        var sourceNodeLocator = ParseSitecoreItemUriField(node.InvariantFields, PublishingConstants.Clones.SourceItem);
        sourceNode = await _itemReadRepo.GetItemNode(sourceNodeLocator.Id, _queryContext).ConfigureAwait(false);

        if (sourceNode == null) break;

        var fieldData = sourceNode.VariantFields.SelectMany(x => x.Value)
                .Concat(sourceNode.InvariantFields)
                .Concat(sourceNode.LanguageVariantFields.SelectMany(x => x.Value));

        fieldData.ForEach(sourcesFieldData.Add);

        node = sourceNode;

      } while (IsClonedItem(sourceNode));

      return sourcesFieldData.AsEnumerable();
    }

    private static IItemNode MergeFieldDataValues(IItemNode node, IReadOnlyCollection<IFieldData> fieldsToMerge)
    {
      bool fieldNotPresent(IEnumerable<IFieldData> fields, IFieldData searchField) =>
         !fields.Any(nodeField => nodeField.FieldId == searchField.FieldId);

      bool varianceMatches(IEnumerable<IFieldData> fields, IFieldData searchField) =>
          fields.Any(nodeField => nodeField.Variance.Version == searchField.Variance.Version &&
                                   nodeField.Variance.Language == searchField.Variance.Language);

      var fieldsValidToMerge = fieldsToMerge
          .GroupBy(f => f.Variance.VarianceType)
          .SelectMany(g =>
          {
            switch (g.Key)
            {
              case VarianceType.Invariant:
                return g.Where(f => fieldNotPresent(node.InvariantFields, f));

              case VarianceType.LanguageVariant:
                return node.LanguageVariantFields
                          .SelectMany(kv => {
                            var langVariance = VarianceInfo.LanguageVariant(kv.Key);
                            return g
                                      .Where(f => varianceMatches(kv.Value, f) && fieldNotPresent(kv.Value, f))
                                      .Select(f => new FieldData(f.FieldId, node.Id, f.RawValue, langVariance));
                          });
              default:
                return node.VariantFields
                          .SelectMany(kv =>
                          {
                            var info = kv.Key.AsInfo();
                            return g
                                      .Where(f => varianceMatches(kv.Value, f) && fieldNotPresent(kv.Value, f))
                                      .Select(f => new FieldData(f.FieldId, node.Id, f.RawValue, info));
                          });
            }
          })
          .ToArray();

      return node.Clone(fieldsValidToMerge);
    }

    private static ItemPublishRestrictions ExtractSharedRestrictions(IItemNode node, bool contentAvailabilityEnabled)
    {
      var validTargets = ParseSitecoreMultipleGuidField(
          node.InvariantFields,
          PublishingConstants.PublishingFields.Shared.PublishingTargets);

      var isPublishable = !ParseSitecoreBoolField(
          node.InvariantFields,
          PublishingConstants.PublishingFields.Shared.NeverPublish,
          false);

      var publishableFrom = ParseSitecoreDateField(
          node.InvariantFields,
          PublishingConstants.PublishingFields.Shared.PublishDate,
          MinUtc);

      var publishableTo = ParseSitecoreDateField(
          node.InvariantFields,
          PublishingConstants.PublishingFields.Shared.UnpublishDate,
          MaxUtc);

      var workflow = ParseSitecoreGuidField(
          node.InvariantFields,
          PublishingConstants.WorkflowFields.Workflow,
          null);

      return new ItemPublishRestrictions(validTargets, isPublishable, contentAvailabilityEnabled, publishableFrom, publishableTo, workflow);
    }

    private Dictionary<IVarianceIdentifier, VariantPublishRestrictions> ExtractVariantRestrictions(IItemNode node, ItemPublishRestrictions itemPublishRestrictions)
    {
      var variantRestrictions = new Dictionary<IVarianceIdentifier, VariantPublishRestrictions>(IdentifierComparer);

      var variantFields = node.VariantFields.ToArray();

      for (int i = 0; i < node.VariantFields.Count; i++)
      {
        var variant = variantFields[i];

        var isPublishable = !ParseSitecoreBoolField(
                variant.Value,
                PublishingConstants.PublishingFields.Versioned.HideVersion,
                false);

        var inPublishableWorkflowStateForTarget = IsInPublishableWorkflowStateForTarget(itemPublishRestrictions, ref variant);

        var validFrom = ParseSitecoreDateField(
            variant.Value,
            PublishingConstants.PublishingFields.Versioned.ValidFrom,
            MinUtc);

        var validTo = ParseSitecoreDateField(
            variant.Value,
            PublishingConstants.PublishingFields.Versioned.ValidTo,
            MaxUtc);

        var nextVariances = variantFields.Where(x => x.Key.Language.Equals(variantFields[i].Key.Language)
                   && x.Key.Version > variantFields[i].Key.Version).ToList();

        if (_contentAvailabilityEnabled && DateTime.Equals(validTo, MaxUtc) && nextVariances.Any())
        {
          var nextVariance = nextVariances.FirstOrDefault(x => x.Key.Version == variantFields[i].Key.Version + 1);

          if (nextVariance.Value != null)
          {
            var nextVarianceWorkflowState = ParseSitecoreGuidField(
                                              nextVariance.Value,
                                              PublishingConstants.WorkflowFields.WorkflowState,
                                              null);

            if (!nextVarianceWorkflowState.HasValue || _publishableStates.ContainsKey(nextVarianceWorkflowState.Value))
            {
              validTo = ParseSitecoreDateField(nextVariance.Value,
                                               PublishingConstants.PublishingFields.Versioned.ValidFrom,
                                               MaxUtc);
            }

            if (VarianceOverriddenByNewerVariance(nextVariances, validFrom) && validTo != MaxUtc)
            {
              isPublishable = false;
            }
          }
        }

        if (_contentAvailabilityEnabled)
        {
          if ((validFrom < itemPublishRestrictions.Sunrise && validTo < itemPublishRestrictions.Sunrise) ||
              (validFrom > itemPublishRestrictions.Sunset && validTo > itemPublishRestrictions.Sunset))
          {
            isPublishable = false;
          }
        }

        variantRestrictions.Add(variant.Key,
            new VariantPublishRestrictions(
                isPublishable,
                _contentAvailabilityEnabled,
                inPublishableWorkflowStateForTarget,
                validFrom,
                validTo));
      }

      return variantRestrictions;
    }

    private Predicate<Guid> IsInPublishableWorkflowStateForTarget(ItemPublishRestrictions itemPublishRestrictions, ref KeyValuePair<IVarianceIdentifier, IReadOnlyCollection<IFieldData>> variant)
    {
      var currentWorkflowState = ParseSitecoreGuidField(
          variant.Value,
          PublishingConstants.WorkflowFields.WorkflowState,
          null);

      // build the func that decides if the item is in a publishCandidate state for a given target.
      Predicate<Guid> inPublishableWorkflowStateForTarget = target => true;

      if (itemPublishRestrictions.Workflow.HasValue && currentWorkflowState.HasValue)
      {
        IPublishableWorkflowState targetState;
        if (_publishableStates.TryGetValue(currentWorkflowState.Value, out targetState))
        {
          inPublishableWorkflowStateForTarget = target => targetState.IsPublishableFor(target);
        }
        else
        {
          inPublishableWorkflowStateForTarget = target => false;
        }
      }

      return inPublishableWorkflowStateForTarget;
    }

    private bool VarianceOverriddenByNewerVariance(IEnumerable<KeyValuePair<IVarianceIdentifier, IReadOnlyCollection<IFieldData>>> nextVariances, DateTime varianceValidFrom)
    {
      return nextVariances.Any(nextVariant =>
      {
        var variantValidFrom = ParseSitecoreDateField(
            nextVariant.Value,
            PublishingConstants.PublishingFields.Versioned.ValidFrom,
            MaxUtc);

        return variantValidFrom < varianceValidFrom;
      });
    }

    private static bool ParseSitecoreBoolField(IEnumerable<IFieldData> fields, Guid fieldId, bool defaultValue)
    {
      bool boolValue = defaultValue;
      var targetField = fields.FirstOrDefault(f => f.FieldId == fieldId);
      if (targetField?.RawValue != null)
      {
        boolValue = targetField.RawValue == "1";
      }

      return boolValue;
    }

    private static DateTime ParseSitecoreDateField(IEnumerable<IFieldData> fields, Guid fieldId, DateTime defaultValue)
    {
      DateTime dateTimeValue = defaultValue;
      var targetField = fields.FirstOrDefault(f => f.FieldId == fieldId);
      if (targetField != null)
      {
        dateTimeValue = ClassicDateUtil.ParseDateTime(targetField.RawValue, defaultValue);
      }

      return dateTimeValue;
    }

    private static Guid? ParseSitecoreGuidField(IEnumerable<IFieldData> fields, Guid fieldId, Guid? defaultValue)
    {
      Guid guidValue;
      var targetField = fields.FirstOrDefault(f => f.FieldId == fieldId);
      if (targetField?.RawValue != null && Guid.TryParse(targetField.RawValue, out guidValue))
      {
        return guidValue;
      }

      return defaultValue;
    }

    private static IItemLocator ParseSitecoreItemUriField(IEnumerable<IFieldData> fields, Guid fieldId)
    {
      var targetField = fields.FirstOrDefault(f => f.FieldId == fieldId);
      if (targetField?.RawValue != null)
      {
        return ItemLocatorUtils.ParseSitecoreItemUri(targetField.RawValue, "not_important");
      }

      return null;
    }

    private static IEnumerable<Guid> ParseSitecoreMultipleGuidField(IEnumerable<IFieldData> fields, Guid fieldId)
    {
      var targetField = fields.FirstOrDefault(f => f.FieldId == fieldId);

      if (targetField?.RawValue == null) yield break;

      foreach (var element in targetField.RawValue.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
      {
        Guid guidValue;

        if (Guid.TryParse(element, out guidValue))
        {
          yield return guidValue;
        }
      }
    }
  }
}