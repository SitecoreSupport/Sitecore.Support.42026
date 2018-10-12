﻿using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sitecore.Framework.Eventing;
using Sitecore.Framework.Publishing;
using Sitecore.Framework.Publishing.Data;
using Sitecore.Framework.Publishing.DataPromotion;
using Sitecore.Framework.Publishing.Item;
using Sitecore.Framework.Publishing.ManifestCalculation;
using Sitecore.Framework.Publishing.ManifestCalculation.TargetProducers;
using Sitecore.Framework.Publishing.PublisherOperations;
using Sitecore.Framework.Publishing.PublishJobQueue;
using Sitecore.Framework.Publishing.TemplateGraph;
using Sitecore.Support.Framework.Publishing.ManifestCalculation;

namespace Sitecore.Support.Framework.Publishing.PublishJobQueue.Handlers
{
  public class IncrementalPublishHandler : Sitecore.Framework.Publishing.PublishJobQueue.Handlers.IncrementalPublishHandler
  {
    public IncrementalPublishHandler(
      IRequiredPublishFieldsResolver requiredPublishFieldsResolver,
      IPublisherOperationService publisherOpsService,
      IPromotionCoordinator promoterCoordinator,
      IEventRegistry eventRegistry,
      ILoggerFactory loggerFactory,
      IApplicationLifetime applicationLifetime,
      PublishJobHandlerOptions options = null) : base(
      requiredPublishFieldsResolver,
      publisherOpsService,
      promoterCoordinator,
      eventRegistry,
      loggerFactory,
      applicationLifetime,
      options ?? new PublishJobHandlerOptions())
    { }

    public IncrementalPublishHandler(
      IRequiredPublishFieldsResolver requiredPublishFieldsResolver,
      IPublisherOperationService publisherOpsService,
      IPromotionCoordinator promoterCoordinator,
      IEventRegistry eventRegistry,
      ILoggerFactory loggerFactory,
      IApplicationLifetime applicationLifetime,
      IConfiguration config) : this(
      requiredPublishFieldsResolver,
      publisherOpsService,
      promoterCoordinator,
      eventRegistry,
      loggerFactory,
      applicationLifetime,
      config.As<PublishJobHandlerOptions>())
    { }

    protected override IPublishCandidateSource CreatePublishCandidateSource(
      PublishContext publishContext,
      ITemplateGraph templateGraph,
      IRequiredPublishFieldsResolver publishingFields)
    {
      return new Sitecore.Support.Framework.Publishing.ManifestCalculation.PublishCandidateSource(
        publishContext.SourceStore.Name,
        publishContext.SourceStore.GetItemReadRepository(),
        publishContext.ItemsRelationshipStore.GetItemRelationshipRepository(),
        templateGraph,
        publishContext.SourceStore.GetWorkflowStateRepository(),
        publishContext.PublishOptions.Languages.Select(Language.Parse).ToArray(),
        _requiredPublishFieldsResolver.PublishingFieldsIds,
        publishingFields.MediaFieldsIds,
        _options.ContentAvailability);
    }

  }
}