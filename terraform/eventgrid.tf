###############################################################################
# Event Grid Subscriptions
#
# MUST be in a separate file gated behind null_resource.deploy_package.
# Azure Event Grid performs a live webhook handshake when a subscription is
# created — the target function must already be deployed and return 200,
# otherwise the subscription creation fails with "Webhook validation failed".
#
# Dependency chain:
#   azurerm_windows_function_app.loader
#     → null_resource.deploy_package   (builds + deploys the zip, waits 60s)
#       → azurerm_eventgrid_system_topic_event_subscription.ingest
###############################################################################

locals {
  eg_subscriptions = {
    bundles = {
      prefix = "/blobServices/default/containers/bundles/"
      fn     = "ImportBundleEventGrid"
    }
    ndjson = {
      prefix = "/blobServices/default/containers/ndjson/"
      fn     = "ImportNDJSONEventGrid"
    }
    zip = {
      prefix = "/blobServices/default/containers/zip/"
      fn     = "ImportZIPEventGrid"
    }
  }
}

resource "azurerm_eventgrid_system_topic_event_subscription" "ingest" {
  for_each            = local.eg_subscriptions
  name                = "fhir-${each.key}-sub"
  system_topic        = azurerm_eventgrid_system_topic.storage.name
  resource_group_name = azurerm_resource_group.main.name

  azure_function_endpoint {
    function_id                       = "${azurerm_windows_function_app.loader.id}/functions/${each.value.fn}"
    max_events_per_batch              = var.eg_max_events_per_batch
    preferred_batch_size_in_kilobytes = var.eg_preferred_batch_size_kb
  }

  included_event_types = ["Microsoft.Storage.BlobCreated"]

  subject_filter {
    subject_begins_with = each.value.prefix
  }

  retry_policy {
    max_delivery_attempts = 30
    event_time_to_live    = 1440
  }

  # !! Critical: must depend on the deployed package, not just the Function App resource.
  # The Function App resource existing in ARM does not mean the functions are live.
  depends_on = [null_resource.deploy_package]
}
