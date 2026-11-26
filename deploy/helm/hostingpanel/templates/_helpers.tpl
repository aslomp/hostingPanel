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
