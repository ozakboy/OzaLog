<script setup lang="ts">
// JSON-LD 結構化資料 — 讓 Google 認得這是 SoftwareApplication
// 出現在 rich result / knowledge graph 機率提升
const jsonLd = {
  '@context': 'https://schema.org',
  '@type': 'SoftwareApplication',
  name: 'OzaLog',
  alternateName: ['OzaLog .NET Logger', 'Ozakboy.NLOG (deprecated, renamed to OzaLog)'],
  applicationCategory: 'DeveloperApplication',
  applicationSubCategory: 'Logging Library',
  operatingSystem: 'Cross-platform (.NET Standard 2.0+, .NET 8/9/10)',
  description:
    'Lean .NET local file logging library with the simplest possible static API. HFT-grade async pipeline (ConcurrentQueue + persistent FileStream pool + cached timestamp + drop-oldest backpressure). Zero NuGet dependencies on net8.0/9.0/10.0. Designed for high-throughput multi-threaded scenarios like cryptocurrency tick streams.',
  url: 'https://ozalog.ozakboy.life/',
  downloadUrl: 'https://www.nuget.org/packages/OzaLog/',
  image: 'https://ozalog.ozakboy.life/logo.png',
  author: {
    '@type': 'Person',
    name: 'ozakboy',
    url: 'https://github.com/ozakboy',
  },
  license: 'https://opensource.org/licenses/MIT',
  programmingLanguage: 'C#',
  offers: {
    '@type': 'Offer',
    price: '0',
    priceCurrency: 'USD',
  },
  softwareVersion: '3.0.1',
}

useHead({
  script: [
    {
      type: 'application/ld+json',
      innerHTML: JSON.stringify(jsonLd),
    },
  ],
})
</script>

<template>
  <NuxtLayout>
    <NuxtPage />
  </NuxtLayout>
</template>
