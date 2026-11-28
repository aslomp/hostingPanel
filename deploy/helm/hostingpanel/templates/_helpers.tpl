{{- define "hostingpanel.name" -}}
{{- printf "%s-hostingpanel" .Release.Name | trunc 48 | trimSuffix "-" -}}
{{- end -}}

{{- define "hostingpanel.apacheConfigExport" -}}
{{- $path := required "kubernetesHosting.apacheConfigPath is required (dedicated NFS export)" .Values.kubernetesHosting.apacheConfigPath -}}
{{- $sites := clean (required "kubernetesHosting.nfsPath is required" .Values.kubernetesHosting.nfsPath) -}}
{{- if or (not (hasPrefix "/" $path)) (eq (clean $path) "/") (ne $path (clean $path)) -}}
{{- fail "kubernetesHosting.apacheConfigPath must be a canonical absolute non-root NFS path" -}}
{{- end -}}
{{- if or (eq $sites "/") (eq $sites $path) (hasPrefix (printf "%s/" $sites) $path) (hasPrefix (printf "%s/" $path) $sites) -}}
{{- fail "Apache configuration NFS export must not overlap customer storage" -}}
{{- end -}}
{{- $path -}}
{{- end -}}

{{- define "hostingpanel.accountingExport" -}}
{{- $path := required "kubernetesHosting.accountingSpool.path is required" .Values.kubernetesHosting.accountingSpool.path -}}
{{- if or (not (hasPrefix "/" $path)) (eq $path "/") (ne $path (clean $path)) -}}{{ fail "Accounting export must be canonical absolute non-root" }}{{- end -}}
{{- range $other := list (clean .Values.kubernetesHosting.nfsPath) (include "hostingpanel.apacheConfigExport" .) -}}
{{- if or (eq $other "/") (eq $other $path) (hasPrefix (printf "%s/" $other) $path) (hasPrefix (printf "%s/" $path) $other) -}}{{ fail "Accounting export must not overlap customer or configuration storage" }}{{- end -}}
{{- end -}}
{{- $path -}}
{{- end -}}
